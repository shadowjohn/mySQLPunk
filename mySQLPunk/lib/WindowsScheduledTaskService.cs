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
    }

    public static class WindowsScheduledTaskService
    {
        private const int TaskTriggerDaily = 2;
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
            if (start <= now) start = start.AddDays(1);

            return new ScheduledTaskRegistrationSpec
            {
                TaskName = GetTaskName(job.Id),
                Description = Localization.Format("Automation.TaskDescription", job.Name),
                ExecutablePath = fullExecutablePath,
                Arguments = ScheduledJobCliService.RunJobCommand + " " + QuoteArgument(fullJobPath),
                WorkingDirectory = Path.GetDirectoryName(fullExecutablePath),
                StartBoundary = start
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

                trigger = ((dynamic)task).Triggers.Create(TaskTriggerDaily);
                ((dynamic)trigger).StartBoundary = spec.StartBoundary.ToString("s", CultureInfo.InvariantCulture);
                ((dynamic)trigger).DaysInterval = 1;
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
