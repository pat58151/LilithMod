using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace LilithMod
{
    /// <summary>Launches apps from an editable allowlist.</summary>
    public static class AppLauncher
    {
        private static string _filePath;
        private static DateTime _lastWrite;
        private static Dictionary<string, string> _targets;
        private static List<string> _names;

        /// <summary>Whether app launching is enabled.</summary>
        public static bool GateOpen =>
            LilithModPlugin.CfgAllowOpenApps != null && LilithModPlugin.CfgAllowOpenApps.Value;

        /// <summary>Opens an allowed app by canonical name.</summary>
        public static bool TryOpen(string appName)
        {
            if (string.IsNullOrWhiteSpace(appName)) return false;
            appName = appName.Trim().ToLowerInvariant();

            EnsureLoaded();

            if (!_targets.TryGetValue(appName, out string target))
            {
                LilithModPlugin.Logger.LogWarning(
                    $"[AppLauncher] Refusing to open '{appName}': not in allowed list. See apps/lilith-apps.txt.");
                return false;
            }

            // Handle default browser resolution
            if (appName == "browser" && target == "default")
            {
                string browserPath = ResolveDefaultBrowser();
                if (!string.IsNullOrEmpty(browserPath))
                {
                    target = browserPath;
                }
                else
                {
                    // Fallback: open Google via shell
                    LilithModPlugin.Logger.LogWarning(
                        "[AppLauncher] Default browser resolution failed; falling back to Google.");
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://www.google.com",
                            UseShellExecute = true
                        });
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LilithModPlugin.Logger.LogWarning(
                            $"[AppLauncher] Launching 'browser' fallback failed: {ex.Message}");
                        return false;
                    }
                }
            }

            LilithModPlugin.Logger.LogInfo(
                $"[AppLauncher] Launching '{appName}' -> '{target}' ...");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                };
                Process.Start(psi);
                LilithModPlugin.Logger.LogInfo($"[AppLauncher] Launching '{appName}' -> success.");
                return true;
            }
            catch (Exception ex)
            {
                // Special retry for discord:// protocol
                if (target.StartsWith("discord:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        LilithModPlugin.Logger.LogWarning(
                            $"[AppLauncher] discord:// failed ({ex.Message}); retrying with https://discord.com/app.");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "https://discord.com/app",
                            UseShellExecute = true
                        });
                        LilithModPlugin.Logger.LogInfo($"[AppLauncher] Launching 'discord' via web fallback -> success.");
                        return true;
                    }
                    catch (Exception retryEx)
                    {
                        LilithModPlugin.Logger.LogWarning(
                            $"[AppLauncher] Launching '{appName}' failed: {retryEx.Message}");
                        return false;
                    }
                }

                LilithModPlugin.Logger.LogWarning(
                    $"[AppLauncher] Launching '{appName}' failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Opens an encoded Google query without reading its results.</summary>
        public static bool TrySearch(string query)
        {
            query = query?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length > 500)
            {
                LilithModPlugin.Logger.LogWarning(
                    "[AppLauncher] Refusing browser search: query is empty or too long.");
                return false;
            }

            string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                LilithModPlugin.Logger.LogInfo(
                    $"[AppLauncher] Opened Google search ({query.Length} characters).");
                return true;
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    "[AppLauncher] Browser search failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Returns current allowed app names in lowercase.</summary>
        public static IReadOnlyList<string> GetAllowedNames()
        {
            EnsureLoaded();
            return _names.AsReadOnly();
        }

        /// <summary>Creates the allowlist if needed and opens its directory.</summary>
        public static void OpenAllowedList()
        {
            EnsureLoaded();
            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (File.Exists(_filePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _filePath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    // Fall back to the containing directory.
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    "[AppLauncher] Could not open allowed‑list: " + ex.Message);
            }
        }

        // ── internal ────────────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string appsDir = Path.Combine(pluginDir, "apps");
            string path = Path.Combine(appsDir, "lilith-apps.txt");

            // Create with defaults if missing.
            if (!File.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(appsDir);
                    string defaults = @"# Lilith allowed apps — one ""name = target"" per line.
# Add your own, e.g.: notepad = notepad.exe
# The special name ""browser"" opens the user's default browser.
discord = discord://
steam = steam://open/main
youtube = https://www.youtube.com
instagram = https://www.instagram.com
twitter = https://x.com
facebook = https://www.facebook.com
reddit = https://www.reddit.com
browser = default
";
                    File.WriteAllText(path, defaults, new System.Text.UTF8Encoding(false));
                    LilithModPlugin.Logger.LogInfo("[AppLauncher] Created default allowed-apps list at " + path);
                }
                catch (Exception ex)
                {
                    LilithModPlugin.Logger.LogWarning("[AppLauncher] Could not create default allowed-apps list: " + ex.Message);
                    _filePath = path;
                    _lastWrite = DateTime.MinValue;
                    _targets = new Dictionary<string, string>();
                    _names = new List<string>();
                    return;
                }
            }

            DateTime currentWrite;
            try { currentWrite = File.GetLastWriteTime(path); }
            catch { currentWrite = DateTime.MinValue; }

            if (_filePath == path && _lastWrite == currentWrite)
                return; // already fresh

            _filePath = path;
            _lastWrite = currentWrite;
            // Swap complete collections for lock-free background reads.
            var targets = new Dictionary<string, string>();
            var names = new List<string>();

            try
            {
                string[] lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq < 0) continue; // malformed, skip

                    string name = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string target = line.Substring(eq + 1).Trim();

                    if (string.IsNullOrEmpty(target))
                    {
                        LilithModPlugin.Logger.LogWarning(
                            $"[AppLauncher] Ignoring empty target for '{name}' in lilith-apps.txt.");
                        continue;
                    }

                    if (targets.ContainsKey(name))
                    {
                        LilithModPlugin.Logger.LogWarning(
                            $"[AppLauncher] Duplicate name '{name}' in lilith-apps.txt; last entry wins.");
                        names.Remove(name);
                    }

                    targets[name] = target;
                    names.Add(name);
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[AppLauncher] Error reading lilith-apps.txt: " + ex.Message);
            }

            _targets = targets;
            _names = names;
        }

        /// <summary>Resolves the default browser from the Windows registry.</summary>
        private static string ResolveDefaultBrowser()
        {
            try
            {
                using (var userChoiceKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice"))
                {
                    if (userChoiceKey == null)
                    {
                        LilithModPlugin.Logger.LogWarning(
                            "[AppLauncher] Default browser UserChoice registry key not found.");
                        return null;
                    }

                    string progId = userChoiceKey.GetValue("Progid") as string;
                    if (string.IsNullOrWhiteSpace(progId))
                    {
                        LilithModPlugin.Logger.LogWarning(
                            "[AppLauncher] Default browser Progid is empty.");
                        return null;
                    }

                    using (var commandKey = Registry.ClassesRoot.OpenSubKey(
                        $@"{progId}\shell\open\command"))
                    {
                        if (commandKey == null)
                        {
                            LilithModPlugin.Logger.LogWarning(
                                $"[AppLauncher] Browser command key for '{progId}' not found.");
                            return null;
                        }

                        string command = commandKey.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(command))
                        {
                            LilithModPlugin.Logger.LogWarning(
                                $"[AppLauncher] Browser command value for '{progId}' is empty.");
                            return null;
                        }

                        command = command.Trim();
                        string exePath;
                        if (command.StartsWith("\"", StringComparison.Ordinal))
                        {
                            int closeQuote = command.IndexOf('"', 1);
                            exePath = closeQuote > 1
                                ? command.Substring(1, closeQuote - 1)
                                : command.Substring(1);
                        }
                        else
                        {
                            int space = command.IndexOf(' ');
                            exePath = space > 0
                                ? command.Substring(0, space)
                                : command;
                        }

                        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                            return exePath;

                        LilithModPlugin.Logger.LogWarning(
                            $"[AppLauncher] Resolved browser path not found: '{exePath}'");
                    }
                }
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning(
                    "[AppLauncher] Default browser registry resolution error: " + ex.Message);
            }

            return null;
        }
    }
}
