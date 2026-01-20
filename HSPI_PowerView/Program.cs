using System;
using HomeSeer.PluginSdk;

namespace HSPI_PowerView
{
    class Program
    {
        static void Main(string[] args)
        {
            var plugin = new HSPI();
            
            try
            {
                Console.WriteLine("Starting PowerView Plugin...");
                plugin.Connect(args);
                Console.WriteLine("PowerView Plugin started successfully.");
                
                // Keep the plugin running
                while (true)
                {
                    System.Threading.Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting PowerView Plugin: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
