using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DokanNet;
using DokanNet.Logging;

namespace OverlayFileSystem
{
    internal class Program
    {
        private const string SmbKey = "-smb";       // ���� � SMB-�����
        private const string OverlayKey = "-overlay"; // ���� ��� ���������� ����
        private const string MountKey = "-where";  // ����� ������������

        private static async Task Main(string[] args)
        {
            args = new string[]
            {
                "-smb=Z:\\",
                "-overlay=E:\\Overlay Write",
                "-where=H:\\"
            };

            if (args.Contains("-help") || args.Contains("/help") || args.Contains("/?"))
            {
                PrintHelp();
                return;
            }
            try
            {
                // ������ ���������� ��������� ������
                var arguments = args
                   .Select(x => x.Split(new char[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries))
                   .ToDictionary(x => x[0], x => x.Length > 1 ? x[1] as object : true, StringComparer.OrdinalIgnoreCase);

                // ���� � ����� � ����� ������������
                var smbPath = arguments.ContainsKey(SmbKey)
                   ? arguments[SmbKey] as string
                   : null;

                var overlayPath = arguments.ContainsKey(OverlayKey)
                   ? arguments[OverlayKey] as string
                   : null;

                var mountPath = arguments.ContainsKey(MountKey)
                   ? arguments[MountKey] as string
                   : null;


                if (!Directory.Exists(smbPath))
                    throw new ArgumentException($"���� � SMB ({smbPath}) �� ����������.");

                if (!Directory.Exists(overlayPath))
                    throw new ArgumentException($"���� � overlay ({overlayPath}) �� ����������.");

                // �������
                using (var overlayLogger = new ConsoleLogger("[OverlayFS] "))
                using (var dokanLogger = new ConsoleLogger("[Dokan] "))
                using (var dokan = new Dokan(dokanLogger))
                {
                    overlayLogger.Debug($"SMB path: {smbPath}");
                    overlayLogger.Debug($"Overlay path: {overlayPath}");
                    overlayLogger.Debug($"Mount point: {mountPath}");

                    // �������� OverlayFileSystem
                    var overlayFs = new OverlayFileSystem(smbPath, overlayPath, overlayLogger);

                    // ������������ � ������������� Dokan
                    var dokanBuilder = new DokanInstanceBuilder(dokan)
                        .ConfigureLogger(() => dokanLogger)
                        .ConfigureOptions(options =>
                        {
                            options.Options = DokanOptions.DebugMode | DokanOptions.EnableNotificationAPI;
                            options.MountPoint = mountPath;
                        });

                    // ������ �������� �������
                    using (var dokanInstance = dokanBuilder.Build(overlayFs))
                    {
                        // ��������� ������� ���������� ���������
                        Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) =>
                        {
                            e.Cancel = true;
                            dokan.RemoveMountPoint(mountPath);
                        };

                        Console.WriteLine($"�������� ������� ������������ �� ������ {mountPath}");
                        await dokanInstance.WaitForFileSystemClosedAsync(uint.MaxValue);
                    }
                }

                Console.WriteLine("������������ ���������.");
            }
            catch (DokanException ex)
            {
                Console.WriteLine("������: " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("�������������� ������: " + ex.Message);
            }
        }

        /// <summary>
        /// ������� ������� �� ���������� ���������.
        /// </summary>
        private static void PrintHelp()
        {
            Console.WriteLine("OverlayFileSystem - ����������� �������� ������� � ����� ������.");
            Console.WriteLine("�������������:");
            Console.WriteLine("  OverlayFileSystem.exe [���������]");
            Console.WriteLine();
            Console.WriteLine("���������:");
            Console.WriteLine("  -smb=<����>       ���� � readonly ���� (��������, SMB-����). �� ���������: Z:\\");
            Console.WriteLine("  -overlay=<����>   ���� � writable ���� (��������� �������). �� ���������: C:\\Overlay");
            Console.WriteLine("  -where=<����>     ����� ������������ ����������� �������� �������. �� ���������: M:\\");
            Console.WriteLine();
            Console.WriteLine("�������:");
            Console.WriteLine("  OverlayFileSystem.exe -smb=Z:\\ -overlay=C:\\Overlay -where=M:\\");
            Console.WriteLine();
            Console.WriteLine("�������:");
            Console.WriteLine("  OverlayFileSystem.exe /?");
            Console.WriteLine("  OverlayFileSystem.exe /help");
            Console.WriteLine("  OverlayFileSystem.exe -help");
        }
    }
}
