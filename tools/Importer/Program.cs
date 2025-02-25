// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.Fhir.Importer
{
    internal static class Program
    {
        public static void Main()
        {
            using var cts = new CancellationTokenSource();

            // Start a background task to listen for key press
            Task.Run(() =>
            {
                Console.WriteLine("Press 'q' to stop the application...");
                while (true)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.Q)
                    {
                        Console.WriteLine("Stopping application...");
                        cts.Cancel();
                        break;
                    }
                }
            });

            try
            {
                Importer.Run(cts.Token);
            }
            catch (OperationCanceledException)
            {
#pragma warning disable CA1303 // Do not pass literals as localized parameters
                Console.WriteLine("Operation canceled. Cleaning up...");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.Out.Flush();
            }
            finally
            {
                Console.WriteLine("Flushing logs and shutting down...");
                Console.Out.Flush();
            }

            Console.WriteLine("Application is complete");
            Console.Out.Flush();
            Task.Delay(10000);
        }
    }
#pragma warning restore CA1303 // Do not pass literals as localized parameters
}
