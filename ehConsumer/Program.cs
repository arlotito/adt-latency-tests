using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Azure.Messaging.EventHubs.Consumer;

using CommandLine;

using System.Diagnostics ; // to use Stopwatch feature
using System.ComponentModel;

namespace EhConsumer
{
    internal class Parameters
    {
        [Option(
            'n',
            "ehName",
            Required = false,
            Default = "",
            HelpText = "The event hub-compatible name of your IoT Hub instance.\nIf you prefer, you can use the env var EH_NAME instead. \nUse `az iot hub show --query properties.eventHubEndpoints.events.path --name {your IoT Hub name}` to fetch via the Azure CLI.")]
        public string EventHubName { get; set; }

        [Option(
            "eh-conn-string",
            Required = false,
            Default = "",
            HelpText = "The connection string to the event hub-compatible endpoint.\nIf you prefer, you can use the env var EH_CONN_STRING instead. Use the Azure portal to get this parameter.")]
        public string EventHubConnectionString { get; set; }
    }

    public class RollingAverage
    {
        double acc, min, max,last;
        int counter;

        public RollingAverage()
        {
            Reset();
        }

        public void Reset()
        {
            acc = 0;
            min = double.MaxValue;
            max = double.MinValue;
            counter = 0;
        }

        public void Add(double value)
        {
            counter++;
            acc += value;
            last = value;
            min = (value < min) ? value : min;
            max = (value > max) ? value : max;
        }

        public double GetAverage()
        {
            return acc / counter;
        }

        public double GetMin()
        {
            return min;
        }

        public double GetMax()
        {
            return max;
        }

        public double GetCounter()
        {
            return counter;
        }

        public double GetLast()
        {
            return last;
        }

        public void WriteStats()
        {
            Console.WriteLine($"last:   {this.GetLast():0} [ms]");
            Console.WriteLine($"avg:    {this.GetAverage():0} [ms]");
            Console.WriteLine($"min:    {this.GetMin():0} [ms]");
            Console.WriteLine($"max:    {this.GetMax():0} [ms]");
        }

        public string WriteStatsShort()
        {
            return String.Format($"{this.GetLast():0} (avg:{this.GetAverage():0.0},  min:{this.GetMin():0}/max:{this.GetMax():0})");
        }

        public string WriteStatsCSV()
        {
            return String.Format($"{this.GetLast():0},{this.GetAverage():0.0},{this.GetMin():0},{this.GetMax():0}");
        }

        public string WriteHeaderCSV(string prefix)
        {
            return String.Format($"{prefix}-last,{prefix}-avg,{prefix}-min,{prefix}-max");
        }
    }

    public class Stats
    {
        public RollingAverage b, c, d, e, e2e;

        public Stats()
        {
            b = new RollingAverage();
            c = new RollingAverage();
            d = new RollingAverage();
            e = new RollingAverage();
            e2e = new RollingAverage();
        }

        public string GetCsvHeader()
        {
            string line = string.Empty;
            line += b.WriteHeaderCSV("b") + ",";
            line += c.WriteHeaderCSV("c") + ",";
            line += d.WriteHeaderCSV("d") + ",";
            line += e.WriteHeaderCSV("e") + ",";
            line += e2e.WriteHeaderCSV("e2e");
            line += "\n";

            return line;
        }

        public string GetCsvValues()
        {
            string line = string.Empty;
            line += b.WriteStatsCSV() + ",";
            line += c.WriteStatsCSV() + ",";
            line += d.WriteStatsCSV() + ",";
            line += e.WriteStatsCSV() + ",";
            line += e2e.WriteStatsCSV();
            line += "\n";

            return line;
        }

    }

    class Program
    {
        private static string _eventHubConnectionString = "";
        private static string _eventHubName = "";

        private static string _csvFilename = "output.log";

        private static Stats _stats = new Stats();

        private static void GetConfig(string[] args)
        {
            Parameters _parameters = new Parameters();

            // Parse application parameters
            ParserResult<Parameters> result = Parser.Default.ParseArguments<Parameters>(args)
                .WithParsed(parsedParams =>
                {
                    _parameters = parsedParams;
                })
                .WithNotParsed(errors =>
                {
                    Environment.Exit(1);
                });

            _eventHubName = Environment.GetEnvironmentVariable("EH_NAME");
            if (!string.IsNullOrEmpty(_parameters.EventHubName))
            {
                _eventHubName = _parameters.EventHubName;
            }

            _eventHubConnectionString = Environment.GetEnvironmentVariable("EH_CONN_STRING");
            if (!string.IsNullOrEmpty(_parameters.EventHubConnectionString))
            {
                _eventHubConnectionString = _parameters.EventHubConnectionString;
            }
        }
        
        public static async Task Main(string[] args)
        {
            GetConfig(args);

            if (string.IsNullOrEmpty(_eventHubName) || string.IsNullOrEmpty(_eventHubConnectionString))
            {
                Console.WriteLine("\n\nPlease provide EH conn string.");
                Environment.Exit(1);
            }

            // Set up a way for the user to gracefully shutdown
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
                Console.WriteLine("\n\nExiting...");
            };

            // create output csv fields
            _csvFilename = DateTime.Now.ToString("yyyyMMddTHHmmss") + _csvFilename;
            await File.WriteAllTextAsync(_csvFilename, _stats.GetCsvHeader());

            // listens to EH messages
            await ListenToEventHubAsync(cts.Token);

            Console.WriteLine("\nCloud message reader finished.");
        }

        // Asynchronously create a PartitionReceiver for a partition and then start
        // reading any messages sent from the simulated client.
        private static async Task ListenToEventHubAsync(CancellationToken ct)
        {
            Stopwatch timer = new Stopwatch();

            int counter = 0;
            double rate;

            DateTime iotHubEnqueuedTime = DateTime.Now, 
                eventGridTriggerTime = DateTime.Now, 
                functionEntrypointTime = DateTime.Now, 
                functionUpdateTime = DateTime.Now;
            
            await using var consumer = new EventHubConsumerClient(
                    EventHubConsumerClient.DefaultConsumerGroupName,
                    _eventHubConnectionString,
                    _eventHubName);

            Console.WriteLine("Listening for messages on all partitions.");
            Console.WriteLine("");

            try
            {
                
                await foreach (PartitionEvent partitionEvent in consumer.ReadEventsAsync(false, null, ct)) //starts reading new events only
                {
                    string data = Encoding.UTF8.GetString(partitionEvent.Data.Body.ToArray());

                    DateTime eventHubEnqueuedTime = partitionEvent.Data.EnqueuedTime.DateTime;
                    
                    string[] lines = data.Split(new string[] { System.Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var line in lines)
                    {
                        if (!timer.IsRunning)
                            timer.Start();
                        
                        counter++;
                        rate = (double)counter / timer.ElapsedMilliseconds * 1000;

                        try
                        {
                            //var msg = JsonConvert.DeserializeObject<AsaMessage>(line);

                            JObject adtData = (JObject)JsonConvert.DeserializeObject(line.ToString());
                            JArray patch = adtData["patch"] as JArray;
                            foreach (JObject item in patch) // <-- Note that here we used JObject instead of usual JProperty
                            {
                                string value = item.GetValue("value").ToString();
                                string path = item.GetValue("path").ToString();
                                string op = item.GetValue("op").ToString();
                                //Console.WriteLine($"{value}, {path}, {op}");

                                switch (path)
                                {
                                    case "/TimeA":
                                        iotHubEnqueuedTime = (DateTime)item.GetValue("value");
                                        break;

                                    case "/TimeB":
                                        eventGridTriggerTime = (DateTime)item.GetValue("value");
                                        break;

                                    case "/TimeC":
                                        functionEntrypointTime = (DateTime)item.GetValue("value");
                                        break;

                                    case "/TimeD":
                                        functionUpdateTime = (DateTime)item.GetValue("value");
                                        break;
                                }
                            }

                            Console.WriteLine();
                            Console.WriteLine("-----------------------------------------");
                            Console.WriteLine($"Received: {line}");
                            
                            Console.WriteLine();
                            Console.WriteLine($"Simulator --A--> IoT Hub --B--> Event Grid --> function --C-auth-D--> ADT --E--> Event Hub ----> console app");

                            Console.WriteLine();
                            TimeSpan b = eventGridTriggerTime - iotHubEnqueuedTime; //b to a
                            TimeSpan c = functionEntrypointTime - eventGridTriggerTime;
                            TimeSpan d = functionUpdateTime - functionEntrypointTime;
                            TimeSpan e = eventHubEnqueuedTime - functionUpdateTime;
                            //TimeSpan fToeTimeSpan = DateTime.UtcNow - eventHubEnqueuedTime;

                            _stats.b.Add(b.TotalMilliseconds);
                            _stats.c.Add(c.TotalMilliseconds);
                            _stats.d.Add(d.TotalMilliseconds);
                            _stats.e.Add(e.TotalMilliseconds);
            
                            Console.WriteLine($"A - IotHubEnqueuedTime:     {iotHubEnqueuedTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")}");
                            Console.WriteLine($"B - EventGridTrigger latency:   {_stats.b.WriteStatsShort()}");
                            Console.WriteLine($"C - FunctionPreAuth latency:    {_stats.c.WriteStatsShort()}");
                            Console.WriteLine($"D - FunctionPostAuth latency:   {_stats.d.WriteStatsShort()}");
                            Console.WriteLine($"E - EventHubEnqueued latency:   {_stats.e.WriteStatsShort()}");
                            //Console.WriteLine($"F - ConsumerTime:           {fToe.TotalMilliseconds}");

                            TimeSpan aToETimeSpan  = eventHubEnqueuedTime - iotHubEnqueuedTime;
                            _stats.e2e.Add(aToETimeSpan.TotalMilliseconds);
                            
                            Console.WriteLine();
                            Console.WriteLine($"A to E latency:                 {_stats.e2e.WriteStatsShort()}");
                            
                            Console.WriteLine();
                            Console.WriteLine($"rate:               {rate:0.000} [event/s]");

                            Console.WriteLine();
                            Console.WriteLine($"Time elapsed:       {timer.ElapsedMilliseconds:0} [ms]");
                            Console.WriteLine($"Events counter:     {counter}") ;

                            File.AppendAllText(_csvFilename, _stats.GetCsvValues());
                        }

                        catch (Newtonsoft.Json.JsonReaderException e)
                        {
                            Console.WriteLine($"JsonReaderException error on: {data}");
                            Console.WriteLine($"{e}");
                            Console.WriteLine($"{e.Message}");
                        }

                        catch (Newtonsoft.Json.JsonSerializationException e)
                        {
                            Console.WriteLine($"JsonSerializationException error on: {data}");
                            Console.WriteLine($"{e}");
                            Console.WriteLine($"{e.Message}");
                        }

                        catch (Exception e)
                        {
                            Console.WriteLine($"{e}");
                        }

                        //timer.Restart();
                    }
                }
            }

            catch (TaskCanceledException)
            {
                // This is expected when the token is signaled; it should not be considered an
                // error in this scenario.
                return;
            }
        }

        private static void PrintProperties(KeyValuePair<string, object> prop)
        {
            string propValue = prop.Value is DateTime
                ? ((DateTime)prop.Value).ToString("O") // using a built-in date format here that includes milliseconds
                : prop.Value.ToString();

            Console.WriteLine($"\t\t{prop.Key}: {propValue}");
        }
    }
}
