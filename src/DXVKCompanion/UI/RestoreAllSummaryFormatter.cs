using System.Collections.Generic;
using System.Linq;
using System.Text;
using DXVKCompanion.Models;

namespace DXVKCompanion.UI
{
    /// <summary>
    /// Pure presentation formatter for Restore All results.
    /// Formats the outcome counts and appends per-game details from summary messages.
    /// </summary>
    public static class RestoreAllSummaryFormatter
    {
        /// <summary>
        /// Formats a RestoreAllSummary into a user-facing completion message.
        /// Preserves the outcome counts heading and appends non-blank per-game details
        /// (refusals, queued running games, exceptions) in their original order.
        /// </summary>
        /// <param name="summary">The restore summary to format.</param>
        /// <returns>The formatted message text.</returns>
        public static string Format(RestoreAllSummary? summary)
        {
            if (summary == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.Append("Restore All completed:\n\n");
            sb.Append($"• Restored: {summary.Restored}\n");
            sb.Append($"• Already clean: {summary.AlreadyRestored}\n");
            sb.Append($"• Queued (games running): {summary.QueuedRunning}\n");
            sb.Append($"• Attention required / failed: {summary.FailedOrAttentionRequired}");

            var nonBlankMessages = summary.Messages?
                .Where(static m => !string.IsNullOrWhiteSpace(m))
                .ToList();

            if (nonBlankMessages != null && nonBlankMessages.Count > 0)
            {
                sb.Append("\n\nDetails:\n");
                sb.Append(string.Join("\n", nonBlankMessages));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Alias for <see cref="Format(RestoreAllSummary?)"/> to format summary results.
        /// </summary>
        public static string FormatSummary(RestoreAllSummary? summary) => Format(summary);
    }
}
