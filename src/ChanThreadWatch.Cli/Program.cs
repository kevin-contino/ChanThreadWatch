using System;

namespace JDP.Cli {
    internal static class Program {
        private static int Main(string[] args) {
            return CliApp.Run(args, Console.Out, Console.Error);
        }
    }
}
