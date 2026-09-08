using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk;

public static partial class SmokeTests
{
    private static void TestVersionedThemeAndLanguagePreferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "mysqlpunk-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestTextPreference(root, typeof(ThemeManager), "_theme", "theme.txt", ThemeManager.Light, ThemeManager.Dark,
                ThemeManager.Load, () => ThemeManager.CurrentTheme, value => ThemeManager.SetTheme(value, true));
            TestTextPreference(root, typeof(Localization), "_language", "language.txt", Localization.TraditionalChinese, Localization.English,
                Localization.Load, () => Localization.CurrentLanguage, value => Localization.SetLanguage(value, true));
        }
        finally
        {
            string resolvedRoot = Path.GetFullPath(root);
            string resolvedTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert(resolvedRoot.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolvedRoot).StartsWith("mysqlpunk-preferences-", StringComparison.Ordinal), "Preference cleanup must remain inside its synthetic temp directory.");
            if (Directory.Exists(resolvedRoot)) Directory.Delete(resolvedRoot, true);
        }
    }

    private static void TestTextPreference(string root, Type type, string valueFieldName, string fileName, string defaultValue,
        string preferredValue, Action load, Func<string> currentValue, Action<string> save)
    {
        BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        PropertyInfo provider = type.GetProperty("SettingsFilePathProvider", flags);
        FieldInfo valueField = type.GetField(valueFieldName, flags);
        FieldInfo failedField = type.GetField("_loadFailed", flags);
        object sync = type.GetField("PreferenceSync", flags).GetValue(null);
        object oldProvider = provider.GetValue(null, null);
        object oldValue = valueField.GetValue(null);
        object oldFailed = failedField.GetValue(null);
        string product = Path.Combine(root, fileName);
        Func<string, string> pathFor = version => Path.Combine(product, version, fileName);
        Action<string, byte[]> write = (path, bytes) => { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes); };
        byte[] original = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(preferredValue + "\r\n")).ToArray();
        Action<string> usePath = path =>
        {
            lock (sync)
            {
                provider.SetValue(null, new Func<string>(() => path), null);
                valueField.SetValue(null, defaultValue);
                failedField.SetValue(null, false);
            }
        };
        try
        {
            write(pathFor("1.0.0.9"), Encoding.UTF8.GetBytes(defaultValue));
            write(pathFor("1.0.0.21"), original);
            write(pathFor("1.0.0.23"), Encoding.UTF8.GetBytes(defaultValue));
            write(pathFor("not-a-version"), Encoding.UTF8.GetBytes(defaultValue));
            string target = pathFor("1.0.0.22");
            usePath(target);
            load();
            AssertEquals(preferredValue, currentValue(), fileName + " must load the nearest numerically earlier preference.");
            Assert(original.SequenceEqual(File.ReadAllBytes(target)), fileName + " migration must retain original BOM and newline bytes.");
            Assert(original.SequenceEqual(File.ReadAllBytes(pathFor("1.0.0.21"))), fileName + " migration must preserve its recovery source.");
            write(target, Encoding.UTF8.GetBytes(defaultValue));
            load();
            AssertEquals(defaultValue, currentValue(), "An existing current " + fileName + " must win over previous versions.");
            AssertEquals(defaultValue, File.ReadAllText(target), "Loading must not rewrite a current preference.");

            byte[][] invalid = { Encoding.UTF8.GetBytes("unsupported"), new byte[0], new byte[129], new byte[] { 0xff }, Encoding.UTF8.GetBytes(preferredValue.ToUpperInvariant()) };
            for (int index = 0; index < invalid.Length; index++)
            {
                string invalidSource = Path.Combine(root, fileName + "-invalid-" + index, "1.0.0.21", fileName);
                string invalidTarget = Path.Combine(root, fileName + "-invalid-" + index, "1.0.0.22", fileName);
                write(invalidSource, invalid[index]);
                usePath(invalidTarget);
                load();
                save(defaultValue);
                Assert(!File.Exists(invalidTarget), "Invalid " + fileName + " must not create a destination, including after automatic save.");
                Assert(invalid[index].SequenceEqual(File.ReadAllBytes(invalidSource)), "Invalid old " + fileName + " must remain unchanged.");
                write(invalidTarget, invalid[index]);
                load();
                save(defaultValue);
                Assert(invalid[index].SequenceEqual(File.ReadAllBytes(invalidTarget)), "Invalid current " + fileName + " must survive Load followed by Save.");
            }

            string future = Path.Combine(root, fileName + "-future", "1.0.0.23", fileName);
            string downgrade = Path.Combine(root, fileName + "-future", "1.0.0.22", fileName);
            write(future, original);
            usePath(downgrade);
            load();
            Assert(!File.Exists(downgrade), "A downgrade must not import a newer " + fileName + ".");

            string raceSource = Path.Combine(root, fileName + "-race", "1.0.0.21", fileName);
            string raceTarget = Path.Combine(root, fileName + "-race", "1.0.0.22", fileName);
            write(raceSource, original);
            usePath(raceTarget);
            using (ManualResetEventSlim entered = new ManualResetEventSlim())
            using (ManualResetEventSlim release = new ManualResetEventSlim())
            using (ManualResetEventSlim saveStarted = new ManualResetEventSlim())
            {
                int calls = 0;
                provider.SetValue(null, new Func<string>(() =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        entered.Set();
                        if (!release.Wait(10000)) throw new TimeoutException("Synthetic preference load was not released.");
                    }
                    return raceTarget;
                }), null);
                Task loading = Task.Run(load);
                Task saving = null;
                try
                {
                    Assert(entered.Wait(5000), "Preference load must enter the controlled overlap point.");
                    saving = Task.Run(() => { saveStarted.Set(); save(preferredValue); });
                    Assert(saveStarted.Wait(5000), "Competing preference save must start.");
                    Assert(!saving.Wait(300) && !File.Exists(raceTarget), "Preference Save must wait for migration to finish.");
                    release.Set();
                    Assert(Task.WaitAll(new[] { loading, saving }, 10000), "Preference Load and Save must complete.");
                    AssertEquals(preferredValue, File.ReadAllText(raceTarget), "Waiting Save must preserve the requested supported preference.");
                    Assert(original.SequenceEqual(File.ReadAllBytes(raceSource)), "Concurrent preference operations must preserve the old bytes.");
                }
                finally
                {
                    release.Set();
                    Assert(Task.WaitAll(new[] { loading, saving }.Where(task => task != null).ToArray(), 10000), "Preference workers must stop before test state restoration.");
                }
            }
            Assert(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length == 0, "Preference migration must clean staging files.");
        }
        finally
        {
            lock (sync)
            {
                provider.SetValue(null, oldProvider, null);
                valueField.SetValue(null, oldValue);
                failedField.SetValue(null, oldFailed);
            }
        }
    }
}
