using System;
using DXVKCompanion.Models;

namespace DXVKCompanion.Monitoring
{
    internal static class ProcessQualification
    {
        public static readonly int[] ScheduledOffsetsSeconds = { 0, 2, 4, 6, 8, 10, 20, 30, 44, 60, 90, 120 };
        public const int MaxAssessments = 12;
        public const int MaxDurationSeconds = 120;

        public static bool IsRecognizedGraphicsApi(GraphicsApi api) =>
            api is GraphicsApi.DX9 or GraphicsApi.DX10 or GraphicsApi.DX11 or GraphicsApi.DX12 or GraphicsApi.Vulkan;
    }

    internal sealed class CandidateProcessState
    {
        public int ProcessId { get; }
        public DateTime ProcessStartTime { get; set; }
        public TimeSpan FirstAssessmentTime { get; }
        public int NextAssessmentIndex { get; set; }
        public int AssessmentsConductedCount { get; set; }
        public bool IsQualified { get; set; }
        public bool IsExpired { get; set; }
        public bool LastInspectionHadFailure { get; set; }
        public DetectionSnapshot? QualifiedSnapshot { get; set; }

        public CandidateProcessState(int processId, DateTime processStartTime, TimeSpan firstAssessmentTime)
        {
            ProcessId = processId;
            ProcessStartTime = processStartTime;
            FirstAssessmentTime = firstAssessmentTime;
            NextAssessmentIndex = 0;
            AssessmentsConductedCount = 0;
            IsQualified = false;
            IsExpired = false;
            LastInspectionHadFailure = false;
        }

        public bool IsAssessmentDue(TimeSpan now)
        {
            if (IsQualified || IsExpired || NextAssessmentIndex >= ProcessQualification.ScheduledOffsetsSeconds.Length)
                return false;

            double elapsedSeconds = (now - FirstAssessmentTime).TotalSeconds;
            return elapsedSeconds >= ProcessQualification.ScheduledOffsetsSeconds[NextAssessmentIndex];
        }

        public void AdvanceAfterAssessment(TimeSpan now)
        {
            AssessmentsConductedCount++;
            NextAssessmentIndex++;

            double elapsedSeconds = (now - FirstAssessmentTime).TotalSeconds;

            // Avoid catch-up bursts by skipping any offset deadlines that have already passed
            while (NextAssessmentIndex < ProcessQualification.ScheduledOffsetsSeconds.Length &&
                   elapsedSeconds >= ProcessQualification.ScheduledOffsetsSeconds[NextAssessmentIndex])
            {
                NextAssessmentIndex++;
            }

            if (NextAssessmentIndex >= ProcessQualification.ScheduledOffsetsSeconds.Length ||
                AssessmentsConductedCount >= ProcessQualification.MaxAssessments)
            {
                IsExpired = true;
            }
        }
    }
}
