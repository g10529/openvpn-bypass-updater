using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class Program
{
    private const string GeneratedRoutesFileName = "bypass-routes-auto.conf";
    private const string DomainsFileName = "domains.txt";

    private static int Main(string[] args)
    {
        bool pauseAtEnd = !args.Any(a => string.Equals(a, "--no-pause", StringComparison.OrdinalIgnoreCase));
        bool dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            string appDirectory = AppContext.BaseDirectory;
            string domainsPath = Path.Combine(appDirectory, DomainsFileName);

            if (!File.Exists(domainsPath))
            {
                throw new FileNotFoundException("domains.txt was not found: " + domainsPath);
            }

            List<string> domains = LoadDomains(domainsPath);
            if (domains.Count == 0)
            {
                throw new InvalidOperationException("domains.txt does not contain any usable domains.");
            }

            OpenVpnSession session = FindCurrentSession();
            string ovpnPath = ResolveOvpnPath(session);
            string ovpnDirectory = Path.GetDirectoryName(ovpnPath);
            if (string.IsNullOrWhiteSpace(ovpnDirectory))
            {
                throw new InvalidOperationException("Could not determine the ovpn directory.");
            }
            string generatedRoutesPath = Path.Combine(ovpnDirectory, GeneratedRoutesFileName);

            Console.WriteLine("Current profile: " + session.ProfileName);
            Console.WriteLine("OVPN file: " + ovpnPath);
            Console.WriteLine("Domain list: " + domainsPath);
            Console.WriteLine();

            RouteBuildResult routeResult = BuildRoutes(domains);
            if (routeResult.TotalIpCount == 0)
            {
                throw new InvalidOperationException("No IPv4 addresses were resolved. Nothing was written.");
            }

            bool routesChanged = false;
            bool includeChanged = false;

            if (dryRun)
            {
                Console.WriteLine("Dry run mode: no file changes and no reconnect will be performed.");
                Console.WriteLine();
            }
            else
            {
                routesChanged = WriteIfChanged(generatedRoutesPath, routeResult.Content);
                includeChanged = EnsureIncludeDirective(ovpnPath, GeneratedRoutesFileName);
            }

            Console.WriteLine("Resolved domains:");
            foreach (DomainResolution resolution in routeResult.Resolutions)
            {
                if (resolution.IpAddresses.Count == 0)
                {
                    Console.WriteLine("  " + resolution.Domain + " -> no IPv4 answers");
                    continue;
                }

                Console.WriteLine("  " + resolution.Domain + " -> " + string.Join(", ", resolution.IpAddresses));
            }

            Console.WriteLine();
            if (dryRun)
            {
                Console.WriteLine("Would update route file: " + generatedRoutesPath);
            }
            else
            {
                Console.WriteLine(routesChanged
                    ? "Updated route file: " + generatedRoutesPath
                    : "Route file unchanged: " + generatedRoutesPath);

                if (includeChanged)
                {
                    Console.WriteLine("Added config bypass-routes-auto.conf to the ovpn file.");
                }
            }

            if (dryRun)
            {
                Console.WriteLine("Would trigger OpenVPN GUI reconnect for: " + session.ProfileName);
            }
            else
            {
                ReconnectWithGui(session.ProfileName);
                Console.WriteLine();
                Console.WriteLine("Triggered OpenVPN GUI reconnect for: " + session.ProfileName);
                Console.WriteLine("Waiting for the routing table to settle...");
                Thread.Sleep(TimeSpan.FromSeconds(6));
            }

            Console.WriteLine();
            Console.WriteLine("Connection diagnostics:");
            PrintDiagnostics(routeResult.Resolutions);
            Console.WriteLine("Done.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed:");
            Console.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            if (pauseAtEnd)
            {
                Console.WriteLine();
                Console.Write("Press Enter to exit...");
                Console.ReadLine();
            }
        }
    }

    private static List<string> LoadDomains(string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(line))
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static OpenVpnSession FindCurrentSession()
    {
        const string query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='openvpn.exe'";
        using (var searcher = new ManagementObjectSearcher(query))
        {
            var sessions = new List<OpenVpnSession>();
            foreach (ManagementObject process in searcher.Get())
            {
                string commandLine = process["CommandLine"] == null ? null : process["CommandLine"].ToString();
                if (string.IsNullOrWhiteSpace(commandLine) || commandLine.IndexOf("--config", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string configArgument = ExtractArgument(commandLine, "--config");
                if (string.IsNullOrWhiteSpace(configArgument))
                {
                    continue;
                }

                uint processId = Convert.ToUInt32(process["ProcessId"]);
                string profileName = Path.GetFileNameWithoutExtension(configArgument);
                string logAppend = ExtractArgument(commandLine, "--log-append");

                sessions.Add(new OpenVpnSession(processId, profileName, configArgument, logAppend));
            }

            if (sessions.Count == 0)
            {
                throw new InvalidOperationException("No active OpenVPN connection was found. Connect VPN first, then run this updater.");
            }

            return sessions
                .OrderByDescending(s => s.ProcessId)
                .First();
        }
    }

    private static string ResolveOvpnPath(OpenVpnSession session)
    {
        if (Path.IsPathRooted(session.ConfigArgument) && File.Exists(session.ConfigArgument))
        {
            return session.ConfigArgument;
        }

        var candidates = new List<string>();
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrWhiteSpace(session.LogPath))
        {
            string logDirectory = Path.GetDirectoryName(session.LogPath);
            string rootDirectory = null;
            if (!string.IsNullOrWhiteSpace(logDirectory))
            {
                DirectoryInfo parent = Directory.GetParent(logDirectory);
                rootDirectory = parent == null ? null : parent.FullName;
            }

            if (!string.IsNullOrWhiteSpace(rootDirectory))
            {
                candidates.Add(Path.Combine(rootDirectory, "config", session.ProfileName, Path.GetFileName(session.ConfigArgument)));
                candidates.Add(Path.Combine(rootDirectory, "config", Path.GetFileName(session.ConfigArgument)));
            }
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "OpenVPN", "config", session.ProfileName, Path.GetFileName(session.ConfigArgument)));
            candidates.Add(Path.Combine(userProfile, "OpenVPN", "config", Path.GetFileName(session.ConfigArgument)));
        }

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "OpenVPN", "config-auto", Path.GetFileName(session.ConfigArgument)));
            candidates.Add(Path.Combine(programFiles, "OpenVPN", "config", session.ProfileName, Path.GetFileName(session.ConfigArgument)));
            candidates.Add(Path.Combine(programFiles, "OpenVPN", "config", Path.GetFileName(session.ConfigArgument)));
        }

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            candidates.Add(Path.Combine(programFilesX86, "OpenVPN", "config-auto", Path.GetFileName(session.ConfigArgument)));
            candidates.Add(Path.Combine(programFilesX86, "OpenVPN", "config", session.ProfileName, Path.GetFileName(session.ConfigArgument)));
        }

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not find the ovpn file for the active profile: " + session.ConfigArgument);
    }

    private static RouteBuildResult BuildRoutes(List<string> domains)
    {
        var lines = new List<string>
        {
            "# Auto-generated by OpenVpnBypassUpdater.exe",
            "# These routes bypass the VPN and use the pre-VPN default gateway.",
            "# Do not edit manually; rerun the updater instead.",
            string.Empty
        };

        var seenIps = new HashSet<string>(StringComparer.Ordinal);
        var resolutions = new List<DomainResolution>();
        int totalIpCount = 0;

        foreach (string domain in domains)
        {
            List<string> ips = ResolveIpv4(domain);
            resolutions.Add(new DomainResolution(domain, ips));

            if (ips.Count == 0)
            {
                lines.Add("# " + domain + " -> no IPv4 answers");
                continue;
            }

            lines.Add("# " + domain);
            foreach (string ip in ips)
            {
                if (!seenIps.Add(ip))
                {
                    continue;
                }

                lines.Add("route " + ip + " 255.255.255.255 net_gateway");
                totalIpCount++;
            }

            lines.Add(string.Empty);
        }

        string content = string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
        return new RouteBuildResult(content, totalIpCount, resolutions);
    }

    private static List<string> ResolveIpv4(string domain)
    {
        try
        {
            return Dns.GetHostAddresses(domain)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(ip => ip, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static bool WriteIfChanged(string path, string content)
    {
        string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.ASCII) : null;
        if (string.Equals(existing, content, StringComparison.Ordinal))
        {
            return false;
        }

        File.WriteAllText(path, content, Encoding.ASCII);
        return true;
    }

    private static bool EnsureIncludeDirective(string ovpnPath, string includeFileName)
    {
        string[] lines = File.ReadAllLines(ovpnPath, Encoding.ASCII);
        string pattern = @"^\s*config\s+(""|')?" + Regex.Escape(includeFileName) + @"(""|')?\s*$";

        if (lines.Any(line => Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase)))
        {
            return false;
        }

        string backupPath = ovpnPath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
        File.Copy(ovpnPath, backupPath, true);

        var newLines = new List<string>(lines);
        if (newLines.Count > 0 && !string.IsNullOrWhiteSpace(newLines[newLines.Count - 1]))
        {
            newLines.Add(string.Empty);
        }

        newLines.Add("# Managed include for generated direct-route bypass rules");
        newLines.Add("config " + includeFileName);
        File.WriteAllLines(ovpnPath, newLines, Encoding.ASCII);
        return true;
    }

    private static void PrintDiagnostics(List<DomainResolution> resolutions)
    {
        foreach (DomainResolution resolution in resolutions)
        {
            if (resolution.IpAddresses.Count == 0)
            {
                Console.WriteLine("  " + resolution.Domain + " -> skipped (no IPv4 answers)");
                continue;
            }

            ConnectionProbe probe = ProbeHttpsConnection(resolution);
            string targetIp = !string.IsNullOrWhiteSpace(probe.RemoteIp)
                ? probe.RemoteIp
                : resolution.IpAddresses[0];
            RouteInfo routeInfo = GetRouteInfo(targetIp);

            var parts = new List<string>();
            parts.Add(resolution.Domain);

            if (probe.Success)
            {
                parts.Add("remote " + probe.RemoteIp);
                parts.Add("local " + probe.LocalIp);
            }
            else
            {
                parts.Add("remote (probe failed, using " + targetIp + ")");
            }

            if (routeInfo != null)
            {
                parts.Add("next hop " + routeInfo.Gateway);
                parts.Add("interface " + routeInfo.InterfaceIp);
                parts.Add("metric " + routeInfo.Metric);
            }
            else
            {
                parts.Add("route lookup unavailable");
            }

            Console.WriteLine("  " + string.Join(" | ", parts));
        }
    }

    private static ConnectionProbe ProbeHttpsConnection(DomainResolution resolution)
    {
        foreach (string ip in resolution.IpAddresses)
        {
            TcpClient client = null;
            try
            {
                client = new TcpClient(AddressFamily.InterNetwork);
                IAsyncResult asyncResult = client.BeginConnect(ip, 443, null, null);
                bool connected = asyncResult.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(1800));
                if (!connected)
                {
                    client.Close();
                    continue;
                }

                client.EndConnect(asyncResult);

                IPEndPoint localEndPoint = client.Client.LocalEndPoint as IPEndPoint;
                IPEndPoint remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                string localIp = localEndPoint == null ? "unknown" : localEndPoint.Address.ToString();
                string remoteIp = remoteEndPoint == null ? ip : remoteEndPoint.Address.ToString();

                client.Close();
                return new ConnectionProbe(true, resolution.Domain, remoteIp, localIp);
            }
            catch
            {
                if (client != null)
                {
                    client.Close();
                }
            }
        }

        return new ConnectionProbe(false, resolution.Domain, null, null);
    }

    private static RouteInfo GetRouteInfo(string destinationIp)
    {
        if (string.IsNullOrWhiteSpace(destinationIp))
        {
            return null;
        }

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "route.exe"),
            Arguments = "print " + destinationIp,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using (Process process = Process.Start(startInfo))
        {
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            string[] lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            string pattern = @"^\s*(?<dest>(?:\d{1,3}\.){3}\d{1,3})\s+(?<mask>(?:\d{1,3}\.){3}\d{1,3})\s+(?<gateway>(?:\d{1,3}\.){3}\d{1,3}|On-link)\s+(?<interface>(?:\d{1,3}\.){3}\d{1,3})\s+(?<metric>\d+)\s*$";

            foreach (string line in lines)
            {
                Match match = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                if (!string.Equals(match.Groups["dest"].Value, destinationIp, StringComparison.Ordinal))
                {
                    continue;
                }

                int metric;
                int.TryParse(match.Groups["metric"].Value, out metric);

                return new RouteInfo(
                    match.Groups["gateway"].Value,
                    match.Groups["interface"].Value,
                    metric);
            }
        }

        return null;
    }

    private static void ReconnectWithGui(string profileName)
    {
        string guiPath = GetOpenVpnGuiPath();

        if (!Process.GetProcessesByName("openvpn-gui").Any())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = guiPath,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }

        RunGuiCommand(guiPath, "--command rescan");
        Thread.Sleep(TimeSpan.FromSeconds(2));
        RunGuiCommand(guiPath, "--command reconnect " + QuoteArgument(profileName));
    }

    private static void RunGuiCommand(string guiPath, string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = guiPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string GetOpenVpnGuiPath()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] candidates = new[]
        {
            string.IsNullOrWhiteSpace(programFiles) ? null : Path.Combine(programFiles, "OpenVPN", "bin", "openvpn-gui.exe"),
            string.IsNullOrWhiteSpace(programFilesX86) ? null : Path.Combine(programFilesX86, "OpenVPN", "bin", "openvpn-gui.exe")
        };

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("openvpn-gui.exe was not found.");
    }

    private static string ExtractArgument(string commandLine, string argumentName)
    {
        string pattern = Regex.Escape(argumentName) + @"\s+(?:""(?<value>[^""]+)""|(?<value>\S+))";
        Match match = Regex.Match(commandLine, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private sealed class OpenVpnSession
    {
        public OpenVpnSession(uint processId, string profileName, string configArgument, string logPath)
        {
            ProcessId = processId;
            ProfileName = profileName;
            ConfigArgument = configArgument;
            LogPath = logPath;
        }

        public uint ProcessId { get; private set; }
        public string ProfileName { get; private set; }
        public string ConfigArgument { get; private set; }
        public string LogPath { get; private set; }
    }

    private sealed class DomainResolution
    {
        public DomainResolution(string domain, List<string> ipAddresses)
        {
            Domain = domain;
            IpAddresses = ipAddresses;
        }

        public string Domain { get; private set; }
        public List<string> IpAddresses { get; private set; }
    }

    private sealed class RouteBuildResult
    {
        public RouteBuildResult(string content, int totalIpCount, List<DomainResolution> resolutions)
        {
            Content = content;
            TotalIpCount = totalIpCount;
            Resolutions = resolutions;
        }

        public string Content { get; private set; }
        public int TotalIpCount { get; private set; }
        public List<DomainResolution> Resolutions { get; private set; }
    }

    private sealed class ConnectionProbe
    {
        public ConnectionProbe(bool success, string domain, string remoteIp, string localIp)
        {
            Success = success;
            Domain = domain;
            RemoteIp = remoteIp;
            LocalIp = localIp;
        }

        public bool Success { get; private set; }
        public string Domain { get; private set; }
        public string RemoteIp { get; private set; }
        public string LocalIp { get; private set; }
    }

    private sealed class RouteInfo
    {
        public RouteInfo(string gateway, string interfaceIp, int metric)
        {
            Gateway = gateway;
            InterfaceIp = interfaceIp;
            Metric = metric;
        }

        public string Gateway { get; private set; }
        public string InterfaceIp { get; private set; }
        public int Metric { get; private set; }
    }
}
