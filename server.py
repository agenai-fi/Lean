"""
FastAPI HTTP Server for Lean Engine
Accepts backtest requests and executes Lean algorithms via subprocess
"""
import json
import subprocess
import uuid
from datetime import datetime
from pathlib import Path
from typing import Dict, Any

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
import uvicorn


app = FastAPI(title="Lean Backtest Server", version="1.0.0")


# Request model
class BacktestRequest(BaseModel):
    code: str  # C# algorithm code
    ticker: str  # Symbol (e.g., "BTCUSDT")
    start_date: str  # Format: "2020-01-01"
    end_date: str  # Format: "2024-12-31"


# Constants
LEAN_ROOT = Path("/Lean")
LEAN_LAUNCHER = LEAN_ROOT / "Launcher/bin/Release/QuantConnect.Lean.Launcher.dll"
STRATEGIES_DIR = Path("/app/strategies")
DATA_DIR = Path("/app/data/market")


@app.get("/health")
def health_check():
    """Health check endpoint"""
    return {
        "status": "healthy",
        "lean_root": str(LEAN_ROOT),
        "lean_launcher_exists": LEAN_LAUNCHER.exists()
    }


@app.post("/backtest")
def run_backtest(request: BacktestRequest) -> Dict[str, Any]:
    """
    Execute Lean backtest with provided C# code

    Returns standardized metrics compatible with eval_alpha format
    """
    # Generate unique workspace ID
    workspace_id = str(uuid.uuid4())
    workspace_dir = STRATEGIES_DIR / f"strategy_{workspace_id}"
    workspace_dir.mkdir(parents=True, exist_ok=True)

    try:
        # Write C# algorithm file
        algo_file = workspace_dir / "Main.cs"
        algo_file.write_text(request.code)

        # Compile C# algorithm to DLL
        dll_path = compile_algorithm(workspace_dir)

        # Create Lean configuration
        config = create_lean_config(
            dll_path=dll_path,
            ticker=request.ticker,
            start_date=request.start_date,
            end_date=request.end_date
        )

        config_file = workspace_dir / "config.json"
        config_file.write_text(json.dumps(config, indent=2))

        # Execute Lean
        result = execute_lean(config_file)

        # Parse and return results
        metrics = parse_lean_results(workspace_dir, result)

        return {
            "success": True,
            "workspace_id": workspace_id,
            "metrics": metrics,
            "ticker": request.ticker,
            "start_date": request.start_date,
            "end_date": request.end_date
        }

    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


def create_lean_config(
    dll_path: Path,
    ticker: str,
    start_date: str,
    end_date: str
) -> Dict[str, Any]:
    """
    Create Lean configuration JSON
    """
    return {
        "environment": "backtesting",
        "algorithm-type-name": "Main",
        "algorithm-language": "CSharp",
        "algorithm-location": str(dll_path),
        "data-folder": str(DATA_DIR),
        "results-destination-folder": str(dll_path.parent),
        "parameters": {
            "ticker": ticker,
            "start-date": start_date,
            "end-date": end_date
        }
    }


def execute_lean(config_file: Path) -> subprocess.CompletedProcess:
    """
    Execute Lean via subprocess

    TODO: Verify correct Lean execution command
    Expected: dotnet QuantConnect.Lean.Launcher.dll --config <path>
    """
    cmd = [
        "dotnet",
        str(LEAN_LAUNCHER),
        "--config",
        str(config_file)
    ]

    result = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        timeout=300  # 5 minute timeout
    )

    if result.returncode != 0:
        raise RuntimeError(
            f"Lean execution failed:\nSTDOUT: {result.stdout}\nSTDERR: {result.stderr}"
        )

    return result


def compile_algorithm(workspace_dir: Path) -> Path:
    """
    Compile C# algorithm to DLL

    Returns path to compiled DLL
    """
    # Create .csproj file
    csproj_content = create_csproj_file()
    csproj_file = workspace_dir / "Strategy.csproj"
    csproj_file.write_text(csproj_content)

    # Run dotnet build
    cmd = ["dotnet", "build", str(csproj_file), "-c", "Release"]
    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            cwd=str(workspace_dir),
            timeout=30
        )
    except subprocess.TimeoutExpired:
        raise RuntimeError(
            f"C# compilation timed out after 30 seconds. "
            f"This usually indicates missing assembly references or circular dependencies. "
            f"Check that ParquetMarketData is properly compiled into QuantConnect.Common.dll"
        )

    if result.returncode != 0:
        raise RuntimeError(
            f"C# compilation failed:\nSTDOUT: {result.stdout}\nSTDERR: {result.stderr}"
        )

    # Return path to compiled DLL
    dll_path = workspace_dir / "bin/Release/net9.0/Strategy.dll"
    if not dll_path.exists():
        raise FileNotFoundError(
            f"Expected DLL not found at {dll_path}. Build output:\n{result.stdout}"
        )

    return dll_path


def create_csproj_file() -> str:
    """
    Create minimal .csproj file that references Lean assemblies and ParquetSharp
    """
    return """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Strategy</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ParquetSharp" Version="18.1.0" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="QuantConnect.Algorithm">
      <HintPath>/Lean/Launcher/bin/Release/QuantConnect.Algorithm.dll</HintPath>
    </Reference>
    <Reference Include="QuantConnect.Common">
      <HintPath>/Lean/Launcher/bin/Release/QuantConnect.Common.dll</HintPath>
    </Reference>
    <Reference Include="QuantConnect.Indicators">
      <HintPath>/Lean/Launcher/bin/Release/QuantConnect.Indicators.dll</HintPath>
    </Reference>
    <Reference Include="Python.Runtime">
      <HintPath>/Lean/Launcher/bin/Release/Python.Runtime.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>"""


def parse_lean_results(workspace_dir: Path, result: subprocess.CompletedProcess) -> Dict[str, float]:
    """
    Parse Lean backtest results and map to eval_alpha format

    TODO: Verify actual Lean output structure
    Expected location: workspace_dir/backtests/*/results.json
    or workspace_dir/results.json

    Expected JSON structure:
    {
        "Statistics": {
            "Total Trades": "104",
            "Total Performance": {"Sharpe Ratio": "1.23", ...},
            "Annual Return": "80.3%",
            "Max Drawdown": "51.0%",
            ...
        }
    }
    """
    # Lean writes results to bin/Release/net9.0/Main.json
    result_paths = [
        workspace_dir / "bin/Release/net9.0/Main.json",
        workspace_dir / "Main.json",
        workspace_dir / "results.json",
    ]

    # Scan for Main.json
    result_paths.extend(workspace_dir.rglob("Main.json"))

    results_file = None
    for path in result_paths:
        if path.exists():
            results_file = path
            break

    if not results_file:
        raise FileNotFoundError(
            f"No Main.json found in {workspace_dir}. "
            f"Check Lean output structure. STDOUT: {result.stdout}"
        )

    # Parse results JSON
    with open(results_file) as f:
        results = json.load(f)

    # Extract cumulative metrics from totalPerformance
    total_perf = results.get("totalPerformance", {})
    portfolio_stats = total_perf.get("portfolioStatistics", {})
    trade_stats = total_perf.get("tradeStatistics", {})

    # Map Lean metrics to eval_alpha format (convert strings to floats)
    return {
        "annualized_return": float(portfolio_stats.get("compoundingAnnualReturn", 0.0)),
        "sharpe_ratio": float(portfolio_stats.get("sharpeRatio", 0.0)),
        "max_drawdown": float(portfolio_stats.get("drawdown", 0.0)),
        "total_trades": int(trade_stats.get("totalNumberOfTrades", 0)),
        "win_rate": float(trade_stats.get("winRate", 0.0)),
        "profit_factor": float(trade_stats.get("profitFactor", 0.0))
    }


if __name__ == "__main__":
    # Run server on port 8001
    uvicorn.run(app, host="0.0.0.0", port=8001)
