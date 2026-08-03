using System;

namespace DocSets
{
    /// <summary>Centralizes observable import job transitions used by the desktop runner.</summary>
    public static class ImportSessionStateMachine
    {
        public static void StartOrResume(ImportSessionState session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.Status = ImportSessionStatus.Running;
            session.StartedAtUtc ??= DateTimeOffset.UtcNow;
            session.CompletedAtUtc = null;
            session.LinkResolutionCompleted = false;
            if (session.OverallProgressPercent >= 100)
                session.OverallProgressPercent = 80;
            session.Stage = "Загрузка структуры OneNote";
        }

        public static void ApplyProgress(ImportSessionState session, int current, int total)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.ProgressTotal = Math.Max(0, total);
            var monotonic = Math.Max(session.ProgressCurrent, Math.Max(0, current));
            session.ProgressCurrent = session.ProgressTotal == 0
                ? monotonic
                : Math.Min(session.ProgressTotal, monotonic);
        }

        public static bool RequestPause(ImportSessionState session, Action cancel)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (session.Status != ImportSessionStatus.Running) return false;
            session.Status = ImportSessionStatus.Pausing;
            session.Stage = "Ожидание безопасной контрольной точки";
            cancel?.Invoke();
            return true;
        }

        public static void CompletePause(ImportSessionState session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.Status = ImportSessionStatus.Paused;
            session.Stage = "Приостановлено";
        }
    }
}
