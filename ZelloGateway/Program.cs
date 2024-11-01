using System;
using System.IO;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

using FneLogLevel = fnecore.LogLevel;
using fnecore.Utility;

using NAudio.Wave;

namespace ZelloGateway
{
    internal class Program
    {
        private static ConfigurationObject config;


        public static ConfigurationObject Configuration => config;

        public static FneLogLevel FneLogLevel
        {
            get;
            private set;
        } = FneLogLevel.INFO;

        public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging(config =>
                {
                    config.ClearProviders();
                    config.AddProvider(new SerilogLoggerProvider(Log.Logger));
                });
                services.AddHostedService<Service>();
            });

        private static void Usage(OptionSet p)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string fileName = Path.GetFileName(assembly.Location);

            Console.WriteLine(string.Format("usage: {0} [-h | --help][-c | --config <path to configuration file>][-l | --log-on-console]",
            Path.GetFileNameWithoutExtension(fileName)));
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }

        static void Main(string[] args)
        {
            const string defaultConfigFile = "config.yml";
            bool showHelp = false, showLogOnConsole = true;
            string configFile = string.Empty;

            OptionSet options = new OptionSet()
            {
                { "h|help", "show this message and exit", v => showHelp = v != null },
                { "c=|config=", "sets the path to the configuration file", v => configFile = v },
                { "l|log-on-console", "shows log on console", v => showLogOnConsole = v != null },
            };

            // attempt to parse the commandline
            try
            {
                options.Parse(args);
            }
            catch (OptionException)
            {
                Console.WriteLine("error: invalid arguments");
                Usage(options);
                Environment.Exit(1);
            }

            // show help?
            if (showHelp)
            {
                Usage(options);
                Environment.Exit(1);
            }

            Assembly assembly = Assembly.GetExecutingAssembly();
            string executingPath = Path.GetDirectoryName(assembly.Location);

            // do we some how have a "null" config file?
            if (configFile == null)
            {
                if (File.Exists(Path.Combine(new string[] { executingPath, defaultConfigFile })))
                    configFile = Path.Combine(new string[] { executingPath, defaultConfigFile });
                else
                {
                    Console.WriteLine("error: cannot read the configuration file");
                    Environment.Exit(1);
                }
            }

            // do we some how have a empty config file?
            if (configFile == string.Empty)
            {
                if (File.Exists(Path.Combine(new string[] { executingPath, defaultConfigFile })))
                    configFile = Path.Combine(new string[] { executingPath, defaultConfigFile });
                else
                {
                    Console.WriteLine("error: cannot read the configuration file");
                    Environment.Exit(1);
                }
            }

            try
            {
                using (FileStream stream = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (TextReader reader = new StreamReader(stream))
                    {
                        string yml = reader.ReadToEnd();

                        // setup the YAML deseralizer for the configuration
                        IDeserializer ymlDeserializer = new DeserializerBuilder()
                            .WithNamingConvention(CamelCaseNamingConvention.Instance)
                            .Build();

                        config = ymlDeserializer.Deserialize<ConfigurationObject>(yml);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"error: cannot read the configuration file, {configFile}\n{e.Message}");
                Environment.Exit(1);
            }

            // setup logging configuration
            LoggerConfiguration logConfig = new LoggerConfiguration();
            logConfig.MinimumLevel.Debug();
            const string logTemplate = "{Level:u1}: {Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Message}{NewLine}{Exception}";

            // setup file logging
            LogEventLevel fileLevel = LogEventLevel.Information;
            switch (config.Log.FileLevel)
            {
                case 1:
                    fileLevel = LogEventLevel.Debug;
                    FneLogLevel = FneLogLevel.DEBUG;
                    break;
                case 2:
                case 3:
                default:
                    fileLevel = LogEventLevel.Information;
                    FneLogLevel = FneLogLevel.INFO;
                    break;
                case 4:
                    fileLevel = LogEventLevel.Warning;
                    FneLogLevel = FneLogLevel.WARNING;
                    break;
                case 5:
                    fileLevel = LogEventLevel.Error;
                    FneLogLevel = FneLogLevel.ERROR;
                    break;
                case 6:
                    fileLevel = LogEventLevel.Fatal;
                    FneLogLevel = FneLogLevel.FATAL;
                    break;
            }

            logConfig.WriteTo.File(Path.Combine(new string[] { config.Log.FilePath, config.Log.FileRoot + "-.log" }), fileLevel, logTemplate, rollingInterval: RollingInterval.Day);

            // setup console logging
            if (showLogOnConsole)
            {
                LogEventLevel dispLevel = LogEventLevel.Information;
                switch (config.Log.DisplayLevel)
                {
                    case 1:
                        dispLevel = LogEventLevel.Debug;
                        FneLogLevel = FneLogLevel.DEBUG;
                        break;
                    case 2:
                    case 3:
                    default:
                        dispLevel = LogEventLevel.Information;
                        FneLogLevel = FneLogLevel.INFO;
                        break;
                    case 4:
                        dispLevel = LogEventLevel.Warning;
                        FneLogLevel = FneLogLevel.WARNING;
                        break;
                    case 5:
                        dispLevel = LogEventLevel.Error;
                        FneLogLevel = FneLogLevel.ERROR;
                        break;
                    case 6:
                        dispLevel = LogEventLevel.Fatal;
                        FneLogLevel = FneLogLevel.FATAL;
                        break;
                }

                logConfig.WriteTo.Console(dispLevel, logTemplate);
            }

            // initialize logger
            Log.Logger = logConfig.CreateLogger();

            try
            {
                CreateHostBuilder(args).Build().Run();
            }
            catch (Exception e)
            {
                Log.Logger.Error(e, "An unhandled exception occurred"); // TODO: make this less terse
            }
        }
    }
}
