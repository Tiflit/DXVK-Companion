using System.Collections.Generic;
using DXVKCompanion.Models;
using DXVKCompanion.UI;
using Xunit;

namespace DXVKCompanion.PhaseATests
{
    public sealed class RestoreAllSummaryFormatterTests
    {
        [Fact]
        public void Format_CountsOnly_WhenMessagesEmpty_DoesNotIncludeDetailsHeading()
        {
            // Acceptance: When there are no nonblank messages, show the counts without an empty details heading.
            var summary = new RestoreAllSummary
            {
                TotalManaged = 3,
                Restored = 2,
                AlreadyRestored = 1,
                QueuedRunning = 0,
                FailedOrAttentionRequired = 0
            };

            var formatted = RestoreAllSummaryFormatter.Format(summary);

            var expected = "Restore All completed:\n\n" +
                           "• Restored: 2\n" +
                           "• Already clean: 1\n" +
                           "• Queued (games running): 0\n" +
                           "• Attention required / failed: 0";

            Assert.Equal(expected, formatted);
            Assert.DoesNotContain("Details", formatted);
        }

        [Fact]
        public void Format_CountsOnly_WhenMessagesContainOnlyBlankEntries_IgnoresBlankEntriesAndOmitsDetailsHeading()
        {
            // Acceptance: Ignore empty/whitespace entries; show counts without empty details heading.
            var summary = new RestoreAllSummary
            {
                TotalManaged = 2,
                Restored = 2,
                AlreadyRestored = 0,
                QueuedRunning = 0,
                FailedOrAttentionRequired = 0
            };
            summary.Messages.Add("");
            summary.Messages.Add("   ");
            summary.Messages.Add("\t");
            summary.Messages.Add("\r\n");

            var formatted = RestoreAllSummaryFormatter.Format(summary);

            var expected = "Restore All completed:\n\n" +
                           "• Restored: 2\n" +
                           "• Already clean: 0\n" +
                           "• Queued (games running): 0\n" +
                           "• Attention required / failed: 0";

            Assert.Equal(expected, formatted);
            Assert.DoesNotContain("Details", formatted);
        }

        [Fact]
        public void Format_WithSpecificRefusal_AppendsDetailsSectionPreservingReason()
        {
            // Acceptance: When Messages contains nonblank entries, append a clearly separated details section
            // including every such message in its original order. Preserve game identification and refusal explanation.
            var summary = new RestoreAllSummary
            {
                TotalManaged = 1,
                Restored = 0,
                AlreadyRestored = 0,
                QueuedRunning = 0,
                FailedOrAttentionRequired = 1
            };
            var refusalMessage = "GameA.exe: Refusing rollback on GameA; missing ownership evidence for actively managed installation.";
            summary.Messages.Add(refusalMessage);

            var formatted = RestoreAllSummaryFormatter.Format(summary);

            var expected = "Restore All completed:\n\n" +
                           "• Restored: 0\n" +
                           "• Already clean: 0\n" +
                           "• Queued (games running): 0\n" +
                           "• Attention required / failed: 1\n\n" +
                           "Details:\n" +
                           refusalMessage;

            Assert.Equal(expected, formatted);
            Assert.Contains("Details:\n", formatted);
            Assert.Contains(refusalMessage, formatted);
        }

        [Fact]
        public void Format_MixedResults_PreservesQueuedAndFailureDetailsInOriginalOrderWithoutConflation()
        {
            // Acceptance: The mixed-result case shows both queued and failed-game details without conflating them.
            // Preserves original order and does not replace specific reasons with generic text.
            var summary = new RestoreAllSummary
            {
                TotalManaged = 4,
                Restored = 1,
                AlreadyRestored = 0,
                QueuedRunning = 1,
                FailedOrAttentionRequired = 2
            };

            var msgQueued = "RunningGame.exe: Game running; restore queued for exit.";
            var msgRefusal = "TamperedGame.exe: Refusing rollback on TamperedGame; target hash mismatch.";
            var msgException = "CrashGame.exe: Exception: Access to the path is denied.";

            summary.Messages.Add(msgQueued);
            summary.Messages.Add(msgRefusal);
            summary.Messages.Add(msgException);

            var formatted = RestoreAllSummaryFormatter.Format(summary);

            Assert.StartsWith("Restore All completed:\n\n", formatted);
            Assert.Contains("• Restored: 1\n", formatted);
            Assert.Contains("• Already clean: 0\n", formatted);
            Assert.Contains("• Queued (games running): 1\n", formatted);
            Assert.Contains("• Attention required / failed: 2", formatted);

            Assert.Contains("\n\nDetails:\n", formatted);

            var queuedIndex = formatted.IndexOf(msgQueued);
            var refusalIndex = formatted.IndexOf(msgRefusal);
            var exceptionIndex = formatted.IndexOf(msgException);

            Assert.True(queuedIndex >= 0);
            Assert.True(refusalIndex > queuedIndex);
            Assert.True(exceptionIndex > refusalIndex);

            // Verified all exact texts preserved verbatim
            Assert.Contains(msgQueued, formatted);
            Assert.Contains(msgRefusal, formatted);
            Assert.Contains(msgException, formatted);
        }

        [Fact]
        public void Format_WithMultilineContent_PreservesAllLinesWithoutTruncation()
        {
            // Acceptance: Support multiline reasons without losing their content.
            var summary = new RestoreAllSummary
            {
                TotalManaged = 1,
                Restored = 0,
                AlreadyRestored = 0,
                QueuedRunning = 0,
                FailedOrAttentionRequired = 1
            };

            var multiline = "GameA.exe: Multiple restore failures occurred:\n  - d3d11.dll: Hash mismatch\n  - dxgi.dll: File in use";
            summary.Messages.Add(multiline);

            var formatted = RestoreAllSummaryFormatter.Format(summary);

            Assert.Contains(multiline, formatted);
            Assert.Contains("  - d3d11.dll: Hash mismatch", formatted);
            Assert.Contains("  - dxgi.dll: File in use", formatted);
        }

        [Fact]
        public void Format_LeavesSummaryAndMessageListUnchanged_ReadOnlyBehavior()
        {
            // Acceptance: The formatter leaves the summary, counts and message list unchanged.
            var summary = new RestoreAllSummary
            {
                TotalManaged = 3,
                Restored = 1,
                AlreadyRestored = 1,
                QueuedRunning = 1,
                FailedOrAttentionRequired = 1
            };
            summary.Messages.Add("Game1.exe: Message 1");
            summary.Messages.Add("Game2.exe: Message 2");

            var originalMessages = new List<string>(summary.Messages);

            var result = RestoreAllSummaryFormatter.Format(summary);

            Assert.NotNull(result);
            Assert.Equal(3, summary.TotalManaged);
            Assert.Equal(1, summary.Restored);
            Assert.Equal(1, summary.AlreadyRestored);
            Assert.Equal(1, summary.QueuedRunning);
            Assert.Equal(1, summary.FailedOrAttentionRequired);
            Assert.Equal(originalMessages.Count, summary.Messages.Count);
            Assert.Equal(originalMessages[0], summary.Messages[0]);
            Assert.Equal(originalMessages[1], summary.Messages[1]);
        }

        [Fact]
        public void Format_NullSummary_ReturnsEmptyString()
        {
            Assert.Equal(string.Empty, RestoreAllSummaryFormatter.Format(null));
        }

        [Fact]
        public void FormatSummary_Alias_ProducesIdenticalOutput()
        {
            var summary = new RestoreAllSummary
            {
                Restored = 1,
                AlreadyRestored = 0,
                QueuedRunning = 0,
                FailedOrAttentionRequired = 0
            };
            summary.Messages.Add("Game.exe: Refused");

            Assert.Equal(RestoreAllSummaryFormatter.Format(summary), RestoreAllSummaryFormatter.FormatSummary(summary));
        }
    }
}
