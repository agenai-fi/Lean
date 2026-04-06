/*
 * Unit tests for BaseResultsHandler log-decoupling fix.
 *
 * Verifies that disk logging (LogStore) is unconditional regardless of network
 * queue pressure (Messages count), and that per-cycle drop warnings replace
 * the former one-time-ever _packetDroppedWarning boolean.
 *
 * AT-1: Disk log receives messages when queue exceeds limit
 * AT-2: No disk log entries lost when queue is below limit
 * AT-3: Drop warning is emitted per cycle, not just once
 * AT-4: Backtesting path (no messageQueueLimit) is unaffected
 */

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Moq;
using NUnit.Framework;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Tests.Engine.Results
{
    /// <summary>
    /// Concrete BaseResultsHandler that does NOT stub AddToLogStore, so LogStore
    /// accumulates entries exactly as the production code path does.
    /// </summary>
    internal class TestableResultHandlerWithRealLogStore : BaseResultsHandler
    {
        public TestableResultHandlerWithRealLogStore(IAlgorithm algorithm)
        {
            Algorithm = algorithm;
            Messages = new ConcurrentQueue<Packet>();
            AlgorithmId = "test-algorithm";
        }

        public void CallProcessAlgorithmLogs(int? messageQueueLimit = null)
            => ProcessAlgorithmLogs(messageQueueLimit);

        public List<LogEntry> GetLogStore() => LogStore;

        protected override void Run() { }
        protected override void StoreResult(Packet packet) { }
        protected override void Sample(string chartName, string seriesName, int seriesIndex,
            SeriesType seriesType, ISeriesPoint value, string unit = "$") { }
    }

    [TestFixture]
    public class BaseResultsHandlerLogDecouplingTests
    {
        private ConcurrentQueue<string> _logMessages;
        private ConcurrentQueue<string> _debugMessages;
        private ConcurrentQueue<string> _errorMessages;
        private TestableResultHandlerWithRealLogStore _handler;

        [SetUp]
        public void SetUp()
        {
            _logMessages = new ConcurrentQueue<string>();
            _debugMessages = new ConcurrentQueue<string>();
            _errorMessages = new ConcurrentQueue<string>();

            var algorithmMock = new Mock<IAlgorithm>();
            algorithmMock.SetupGet(a => a.LogMessages).Returns(_logMessages);
            algorithmMock.SetupGet(a => a.DebugMessages).Returns(_debugMessages);
            algorithmMock.SetupGet(a => a.ErrorMessages).Returns(_errorMessages);

            _handler = new TestableResultHandlerWithRealLogStore(algorithmMock.Object);
        }

        /// <summary>
        /// AT-1: Messages always reach LogStore regardless of Messages.Count exceeding the limit.
        /// </summary>
        [Test]
        public void AT1_DiskLogReceivesMessagesWhenQueueExceedsLimit()
        {
            // Pre-fill Messages with 501 entries to trigger rate limiting from the first message
            for (int i = 0; i < 501; i++)
                _handler.Messages.Enqueue(new LogPacket("test-algorithm", $"prefill-{i}"));

            for (int i = 0; i < 10; i++)
                _logMessages.Enqueue($"log-message-{i}");

            var messagesBefore = _handler.Messages.Count;
            _handler.CallProcessAlgorithmLogs(messageQueueLimit: 500);

            // All 10 messages must be in LogStore (disk write is unconditional)
            var logStore = _handler.GetLogStore();
            Assert.That(logStore.Count, Is.EqualTo(10), "All messages must reach LogStore when queue is over limit");

            // Messages queue must NOT have grown by 10 LogPackets for the dropped messages;
            // it should only have grown by 1 (the drop warning HandledErrorPacket)
            var messagesAfter = _handler.Messages.Count;
            Assert.That(messagesAfter, Is.EqualTo(messagesBefore + 1),
                "Only the drop warning packet should be added to Messages, not the 10 dropped log packets");

            var allPackets = new List<Packet>();
            while (_handler.Messages.TryDequeue(out var pkt))
                allPackets.Add(pkt);

            var dropWarnings = allPackets.OfType<HandledErrorPacket>().ToList();
            Assert.That(dropWarnings.Count, Is.GreaterThanOrEqualTo(1), "Drop warning HandledErrorPacket must be present");
            Assert.That(dropWarnings.Any(p => p.Message.Contains("10")), Is.True,
                "Drop warning must mention the count of dropped packets (10)");
        }

        /// <summary>
        /// AT-2: Below-limit messages reach both LogStore and Messages queue.
        /// </summary>
        [Test]
        public void AT2_BelowLimitMessagesReachBothLogStoreAndMessages()
        {
            for (int i = 0; i < 10; i++)
                _logMessages.Enqueue($"log-message-{i}");

            _handler.CallProcessAlgorithmLogs(messageQueueLimit: 500);

            var logStore = _handler.GetLogStore();
            Assert.That(logStore.Count, Is.EqualTo(10), "All 10 messages must be in LogStore");

            var packets = new List<Packet>();
            while (_handler.Messages.TryDequeue(out var pkt))
                packets.Add(pkt);

            var logPackets = packets.OfType<LogPacket>().ToList();
            Assert.That(logPackets.Count, Is.EqualTo(10), "All 10 messages must be enqueued as LogPackets");

            var dropWarnings = packets.OfType<HandledErrorPacket>().ToList();
            Assert.That(dropWarnings.Count, Is.EqualTo(0), "No drop warning should be emitted when below limit");
        }

        /// <summary>
        /// AT-3: Per-cycle drop count is emitted as HandledErrorPacket and resets each cycle.
        /// </summary>
        [Test]
        public void AT3_DropWarningIsEmittedPerCycleAndResetsEachCycle()
        {
            for (int i = 0; i < 501; i++)
                _handler.Messages.Enqueue(new LogPacket("test-algorithm", $"prefill-{i}"));

            // Cycle 1: 5 messages
            for (int i = 0; i < 5; i++)
                _logMessages.Enqueue($"cycle1-msg-{i}");

            _handler.CallProcessAlgorithmLogs(messageQueueLimit: 500);

            HandledErrorPacket cycle1Warning = null;
            var remaining = new List<Packet>();
            while (_handler.Messages.TryDequeue(out var pkt))
            {
                if (pkt is HandledErrorPacket hep && cycle1Warning == null)
                    cycle1Warning = hep;
                else
                    remaining.Add(pkt);
            }

            Assert.That(cycle1Warning, Is.Not.Null, "Cycle 1 must emit a drop warning");
            Assert.That(cycle1Warning.Message, Does.Contain("5"), "Cycle 1 drop warning must mention count 5");

            // Restore Messages to overloaded state
            foreach (var pkt in remaining)
                _handler.Messages.Enqueue(pkt);
            while (_handler.Messages.Count < 501)
                _handler.Messages.Enqueue(new LogPacket("test-algorithm", "prefill-extra"));

            // Cycle 2: 3 fresh messages
            for (int i = 0; i < 3; i++)
                _logMessages.Enqueue($"cycle2-msg-{i}");

            _handler.CallProcessAlgorithmLogs(messageQueueLimit: 500);

            HandledErrorPacket cycle2Warning = null;
            while (_handler.Messages.TryDequeue(out var pkt))
            {
                if (pkt is HandledErrorPacket hep2 && cycle2Warning == null)
                    cycle2Warning = hep2;
            }

            Assert.That(cycle2Warning, Is.Not.Null, "Cycle 2 must emit a drop warning");
            Assert.That(cycle2Warning.Message, Does.Contain("3"),
                "Cycle 2 drop warning must mention count 3 (not accumulated from cycle 1)");
        }

        /// <summary>
        /// AT-4: Backtesting path (no messageQueueLimit) is unaffected.
        /// </summary>
        [Test]
        public void AT4_BacktestingPathWithNoLimitIsUnaffected()
        {
            for (int i = 0; i < 1000; i++)
                _handler.Messages.Enqueue(new LogPacket("test-algorithm", $"prefill-{i}"));

            for (int i = 0; i < 10; i++)
                _logMessages.Enqueue($"log-message-{i}");

            var messagesBefore = _handler.Messages.Count;
            _handler.CallProcessAlgorithmLogs(); // no limit -- backtesting path

            var logStore = _handler.GetLogStore();
            Assert.That(logStore.Count, Is.EqualTo(10), "All 10 messages must be in LogStore");

            var messagesAfter = _handler.Messages.Count;
            Assert.That(messagesAfter, Is.EqualTo(messagesBefore + 10),
                "All 10 messages must be added to Messages queue (no rate limiting in backtesting path)");

            var packets = new List<Packet>();
            while (_handler.Messages.TryDequeue(out var pkt))
                packets.Add(pkt);

            var dropWarnings = packets.OfType<HandledErrorPacket>().ToList();
            Assert.That(dropWarnings.Count, Is.EqualTo(0),
                "No drop warning should be emitted when no messageQueueLimit is set");
        }
    }
}
