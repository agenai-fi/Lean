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
async def health_check():
    """Health check endpoint"""
    return {
        "status": "healthy",
        "lean_root": str(LEAN_ROOT),
        "lean_launcher_exists": LEAN_LAUNCHER.exists()
    }


@app.post("/backtest")
async def run_backtest(request: BacktestRequest) -> Dict[str, Any]:
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

        # Create Lean configuration
        config = create_lean_config(
            workspace_dir=workspace_dir,
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
    workspace_dir: Path,
    ticker: str,
    start_date: str,
    end_date: str
) -> Dict[str, Any]:
    """
    Create Lean configuration JSON

    TODO: Verify these config keys match actual Lean requirements
    """
    return {
        "environment": "backtesting",
        "algorithm-type-name": "Main",
        "algorithm-language": "CSharp",
        "algorithm-location": str(workspace_dir / "Main.cs"),
        "data-folder": str(DATA_DIR),
        "results-destination-folder": str(workspace_dir),
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
    # Try common result file locations
    result_paths = [
        workspace_dir / "results.json",
        workspace_dir / "backtests" / "latest" / "results.json",
    ]

    # Scan for any results.json file
    result_paths.extend(workspace_dir.rglob("results.json"))

    results_file = None
    for path in result_paths:
        if path.exists():
            results_file = path
            break

    if not results_file:
        raise FileNotFoundError(
            f"No results.json found in {workspace_dir}. "
            f"Check Lean output structure. STDOUT: {result.stdout}"
        )

    # Parse results JSON
    with open(results_file) as f:
        results = json.load(f)

    # Extract metrics (TODO: verify actual keys)
    stats = results.get("Statistics", {})

    # Map Lean metrics to eval_alpha format
    # TODO: Verify exact key names in Lean Statistics dictionary
    return {
        "annualized_return": parse_percentage(stats.get("Annual Return", "0%")),
        "sharpe_ratio": parse_float(stats.get("Sharpe Ratio", "0")),
        "max_drawdown": parse_percentage(stats.get("Max Drawdown", "0%")),
        "total_trades": parse_int(stats.get("Total Trades", "0")),
        "win_rate": parse_percentage(stats.get("Win Rate", "0%")),
        "profit_factor": parse_float(stats.get("Profit Factor", "0"))
    }


def parse_percentage(value: str) -> float:
    """Convert percentage string to decimal (e.g., "80.3%" -> 0.803)"""
    return float(value.rstrip('%')) / 100.0


def parse_float(value: str) -> float:
    """Parse float from string, handling empty/invalid values"""
    try:
        return float(value)
    except (ValueError, TypeError):
        return 0.0


def parse_int(value: str) -> int:
    """Parse int from string, handling empty/invalid values"""
    try:
        return int(value)
    except (ValueError, TypeError):
        return 0


if __name__ == "__main__":
    # Run server on port 8001
    uvicorn.run(app, host="0.0.0.0", port=8001)
