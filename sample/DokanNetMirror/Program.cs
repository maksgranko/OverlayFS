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
        private const string SmbKey = "-smb";       // Путь к SMB-диску
        private const string OverlayKey = "-overlay"; // Путь для локального слоя
        private const string MountKey = "-where";  // Точка монтирования

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
                // Чтение аргументов командной строки
                var arguments = args
                   .Select(x => x.Split(new char[] { '=' }, 2, StringSplitOptions.RemoveEmptyEntries))
                   .ToDictionary(x => x[0], x => x.Length > 1 ? x[1] as object : true, StringComparer.OrdinalIgnoreCase);

                // Пути к слоям и точке монтирования
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
                    throw new ArgumentException($"Путь к SMB ({smbPath}) не существует.");

                if (!Directory.Exists(overlayPath))
                    throw new ArgumentException($"Путь к overlay ({overlayPath}) не существует.");

                // Логгеры
                using (var overlayLogger = new ConsoleLogger("[OverlayFS] "))
                using (var dokanLogger = new ConsoleLogger("[Dokan] "))
                using (var dokan = new Dokan(dokanLogger))
                {
                    overlayLogger.Debug($"SMB path: {smbPath}");
                    overlayLogger.Debug($"Overlay path: {overlayPath}");
                    overlayLogger.Debug($"Mount point: {mountPath}");

                    // Создание OverlayFileSystem
                    var overlayFs = new OverlayFileSystem(smbPath, overlayPath, overlayLogger);

                    // Конфигурация и инициализация Dokan
                    var dokanBuilder = new DokanInstanceBuilder(dokan)
                        .ConfigureLogger(() => dokanLogger)
                        .ConfigureOptions(options =>
                        {
                            options.Options = DokanOptions.DebugMode | DokanOptions.EnableNotificationAPI;
                            options.MountPoint = mountPath;
                        });

                    // Запуск файловой системы
                    using (var dokanInstance = dokanBuilder.Build(overlayFs))
                    {
                        // Обработка события завершения программы
                        Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) =>
                        {
                            e.Cancel = true;
                            dokan.RemoveMountPoint(mountPath);
                        };

                        Console.WriteLine($"Файловая система смонтирована по адресу {mountPath}");
                        await dokanInstance.WaitForFileSystemClosedAsync(uint.MaxValue);
                    }
                }

                Console.WriteLine("Монтирование завершено.");
            }
            catch (DokanException ex)
            {
                Console.WriteLine("Ошибка: " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Непредвиденная ошибка: " + ex.Message);
            }
        }

        /// <summary>
        /// Выводит справку по параметрам программы.
        /// </summary>
        private static void PrintHelp()
        {
            Console.WriteLine("OverlayFileSystem - виртуальная файловая система с двумя слоями.");
            Console.WriteLine("Использование:");
            Console.WriteLine("  OverlayFileSystem.exe [параметры]");
            Console.WriteLine();
            Console.WriteLine("Параметры:");
            Console.WriteLine("  -smb=<путь>       Путь к readonly слою (например, SMB-диск). По умолчанию: Z:\\");
            Console.WriteLine("  -overlay=<путь>   Путь к writable слою (локальный каталог). По умолчанию: C:\\Overlay");
            Console.WriteLine("  -where=<путь>     Точка монтирования виртуальной файловой системы. По умолчанию: M:\\");
            Console.WriteLine();
            Console.WriteLine("Примеры:");
            Console.WriteLine("  OverlayFileSystem.exe -smb=Z:\\ -overlay=C:\\Overlay -where=M:\\");
            Console.WriteLine();
            Console.WriteLine("Справка:");
            Console.WriteLine("  OverlayFileSystem.exe /?");
            Console.WriteLine("  OverlayFileSystem.exe /help");
            Console.WriteLine("  OverlayFileSystem.exe -help");
        }
    }
}
