using System;
using System.Text;
using System.Threading;
using System.Windows.Threading;
namespace CodexIsland
{
    // Diagnostic fault-trigger loop only. This is not the 79-suite regression
    // coordinator and a normal exit does not establish a fix or full coverage.
    internal static class NativeLayoutLoopEntry
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // The collector passes its standard --suite argument; this entry
            // deliberately runs only the complete original tool-layout suite.
            Console.OutputEncoding = new UTF8Encoding(false);
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                for (int iteration = 1; iteration <= 8; iteration++)
                {
                    Console.WriteLine("DIAGNOSTIC ITERATION " + iteration + " BEGIN");
                    ToolPageLayoutTests.Run();
                    Console.WriteLine("DIAGNOSTIC ITERATION " + iteration + " END");
                }
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
    }
}
