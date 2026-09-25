using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace mySQLPunk.lib
{
    public sealed class ScheduledTaskRegistrationSpec
    {
        public string TaskName { get; set; }
        public string Description { get; set; }
        public string ExecutablePath { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public DateTime StartBoundary { get; set; }
        public ScheduledJobScheduleKind Kind { get; set; }
        /// <summary>Task Scheduler 的星期位元（1=週日、2=週一…64=週六）。</summary>
        public int DaysOfWeekMask { get; set; }
        /// <summary>每 N 小時重複的 ISO 8601 間隔（例如 PT2H）。</summary>
        public string RepetitionInterval { get; set; }
    }

    public static class WindowsScheduledTaskService
    {
        private const int TaskTriggerTime = 1;
        private const int TaskTriggerDaily = 2;
        private const int TaskTriggerWeekly = 3;
        private const int TaskTriggerLogon = 9;
        private const int TaskActionExecute = 0;
        private const int TaskCreateOrUpdate = 6;
        private const int TaskLogonInteractiveToken = 3;
        private const int TaskRunLevelLeastPrivilege = 0;
        private const int TaskInstancesIgnoreNew = 2;

        public static string GetTaskName(string jobId)
        {
            Guid parsed;
            if (!Guid.TryParse(jobId, out parsed)) throw new InvalidOperationException(Localization.T("Automation.InvalidJobId"));
            return "mySQLPunk - " + parsed.ToString("N");
        }

        public static ScheduledTaskRegistrationSpec BuildRegistration(
            ScheduledJobDefinition job,
            string executablePath,
            string jobPath,
            DateTime now)
        {
            ScheduledJobValidator.Validate(job);
            if (!job.ScheduleEnabled) throw new InvalidOperationException(Localization.T("Automation.ScheduleDisabled"));
            if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException(Localization.T("Automation.ExecutablePathRequired"), "executablePath");
            if (string.IsNullOrWhiteSpace(jobPath)) throw new ArgumentException(Localization.T("Automation.JobPathRequired"), "jobPath");

            string fullExecutablePath = Path.GetFullPath(executablePath);
            string fullJobPath = Path.GetFullPath(jobPath);
            TimeSpan dailyTime = TimeSpan.ParseExact(job.DailyTime, "hh\\:mm", CultureInfo.InvariantCulture);
            DateTime start = now.Date.Add(dailyTime);
            int mask = 0;
            string interval = null;
            switch (job.ScheduleKind)
            {
                case ScheduledJobScheduleKind.Weekly:
                    foreach (DayOfWeek day in job.WeekDays) mask |= 1 << (int)day;
                    // 從今天起找第一個「勾選的星期且時間未過」的日期當起點。
                    while (start <= now || (mask & (1 << (int)start.DayOfWeek)) == 0) start = start.AddDays(1);
                    break;
                case ScheduledJobScheduleKind.Hourly:
                    interval = "PT" + job.IntervalHours.ToString(CultureInfo.InvariantCulture) + "H";
                    while (start <= now) start = start.AddHours(job.IntervalHours);
                    break;
                case ScheduledJobScheduleKind.Logon:
                    start = now;
                    break;
                default:
                    if (start <= now) start = start.AddDays(1);
                    break;
            }

            return new ScheduledTaskRegistrationSpec
            {
                TaskName = GetTaskName(job.Id),
                Description = Localization.Format("Automation.TaskDescription", job.Name),
                ExecutablePath = fullExecutablePath,
                Arguments = ScheduledJobCliService.RunJobCommand + " " + QuoteArgument(fullJobPath),
                WorkingDirectory = Path.GetDirectoryName(fullExecutablePath),
                StartBoundary = start,
                Kind = job.ScheduleKind,
                DaysOfWeekMask = mask,
                RepetitionInterval = interval
            };
        }

        public static void Register(ScheduledJobDefinition job, string executablePath, string jobPath)
        {
            EnsureWindows();
            ScheduledTaskRegistrationSpec spec = BuildRegistration(job, executablePath, jobPath, DateTime.Now);
            object service = null;
            object folder = null;
            object task = null;
            object trigger = null;
            object action = null;
            object registeredTask = null;
            try
            {
                service = CreateService();
                ((dynamic)service).Connect();
                folder = ((dynamic)service).GetFolder("\\");
                task = ((dynamic)service).NewTask(0);

                ((dynamic)task).RegistrationInfo.Description = spec.Description;
                ((dynamic)task).Settings.Enabled = true;
                ((dynamic)task).Settings.StartWhenAvailable = true;
                ((dynamic)task).Settings.DisallowStartIfOnBatteries = false;
                ((dynamic)task).Settings.StopIfGoingOnBatteries = false;
                ((dynamic)task).Settings.MultipleInstances = TaskInstancesIgnoreNew;
                ((dynamic)task).Settings.ExecutionTimeLimit = "PT12H";
                ((dynamic)task).Principal.LogonType = TaskLogonInteractiveToken;
                ((dynamic)task).Principal.RunLevel = TaskRunLevelLeastPrivilege;

                switch (spec.Kind)
                {
                    case ScheduledJobScheduleKind.Weekly:
                        trigger = ((dynamic)task).Triggers.Create(TaskTriggerWeekly);
                        ((dynamic)trigger).DaysOfWeek = (short)spec.DaysOfWeekMask;
                        ((dynamic)trigger).WeeksInterval = (short)1;
                        break;
                    case ScheduledJobScheduleKind.Hourly:
                        trigger = ((dynamic)task).Triggers.Create(TaskTriggerTime);
                        ((dynamic)trigger).Repetition.Interval = spec.RepetitionInterval;
                        ((dynamic)trigger).Repetition.Duration = string.Empty;
                        break;
                    case ScheduledJobScheduleKind.Logon:
                        trigger = ((dynamic)task).Triggers.Create(TaskTriggerLogon);
                        ((dynamic)trigger).UserId = Environment.UserDomainName + "\\" + Environment.UserName;
                        ((dynamic)trigger).Delay = "PT1M";
                        break;
                    default:
                        trigger = ((dynamic)task).Triggers.Create(TaskTriggerDaily);
                        ((dynamic)trigger).DaysInterval = (short)1;
                        break;
                }
                ((dynamic)trigger).StartBoundary = spec.StartBoundary.ToString("s", CultureInfo.InvariantCulture);
                ((dynamic)trigger).Enabled = true;

                action = ((dynamic)task).Actions.Create(TaskActionExecute);
                ((dynamic)action).Path = spec.ExecutablePath;
                ((dynamic)action).Arguments = spec.Arguments;
                ((dynamic)action).WorkingDirectory = spec.WorkingDirectory;

                registeredTask = ((dynamic)folder).RegisterTaskDefinition(
                    spec.TaskName,
                    task,
                    TaskCreateOrUpdate,
                    null,
                    null,
                    TaskLogonInteractiveToken,
                    null);
            }
            finally
            {
                ReleaseComObject(registeredTask);
                ReleaseComObject(action);
                ReleaseComObject(trigger);
                ReleaseComObject(task);
                ReleaseComObject(folder);
                ReleaseComObject(service);
            }
        }

        public static bool IsRegistered(string jobId)
        {
            EnsureWindows();
            object service = null;
            object folder = null;
            object task = null;
            try
            {
                service = CreateService();
                ((dynamic)service).Connect();
                folder = ((dynamic)service).GetFolder("\\");
                try
                {
                    task = ((dynamic)folder).GetTask(GetTaskName(jobId));
                    return task != null;
                }
                catch (Exception ex)
                {
                    if (IsTaskMissing(ex)) return false;
                    throw;
                }
            }
            finally
            {
                ReleaseComObject(task);
                ReleaseComObject(folder);
                ReleaseComObject(service);
            }
        }

        public static void Delete(string jobId)
        {
            EnsureWindows();
            object service = null;
            object folder = null;
            try
            {
                service = CreateService();
                ((dynamic)service).Connect();
                folder = ((dynamic)service).GetFolder("\\");
                try
                {
                    ((dynamic)folder).DeleteTask(GetTaskName(jobId), 0);
                }
                catch (Exception ex)
                {
                    if (!IsTaskMissing(ex)) throw;
                }
            }
            finally
            {
                ReleaseComObject(folder);
                ReleaseComObject(service);
            }
        }

        private static object CreateService()
        {
            Type type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) throw new InvalidOperationException(Localization.T("Automation.TaskSchedulerUnavailable"));
            return Activator.CreateInstance(type);
        }

        private static void EnsureWindows()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new PlatformNotSupportedException(Localization.T("Automation.TaskSchedulerWindowsOnly"));
            }
        }

        private static bool IsTaskMissing(Exception exception)
        {
            int errorCode = exception == null ? 0 : Marshal.GetHRForException(exception);
            return errorCode == unchecked((int)0x80070002) || errorCode == unchecked((int)0x8004130F);
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void ReleaseComObject(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }
}
