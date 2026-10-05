using System;
using System.Collections.Generic;

namespace JDP.Cli {
    internal enum CliCommandKind {
        Help,
        // "--help" after a command: that command's usage
        CommandHelp,
        // No arguments at all: the usage is shown as an error
        MissingCommand,
        Version,
        List,
        Add,
        Remove,
        Watch
    }

    internal sealed class CliCommand {
        public CliCommandKind Kind { get; set; }
        public string Url { get; set; }
        // Null when the option is not given
        public string Description { get; set; }
        public string Category { get; set; }
        // The command's usage, for CommandHelp
        public string HelpText { get; set; }
    }

    // The command line: "ctw --help", "ctw --version", or a command followed by its positional arguments and its
    // "--name value" options, in any order. Each option may be given once, and its value may not start with "--".
    // "--help" (or "-h") anywhere shows the command's usage, and after "--" every argument is a positional value.
    internal static class CliArguments {
        public const string DescriptionOption = "--description";
        public const string CategoryOption = "--category";
        private const string EndOfOptions = "--";

        private sealed class CommandSpec {
            public CliCommandKind Kind;
            public string Usage;
            public int PositionalCount;
            public string[] Options;
        }

        // What has been read of a command's arguments
        private sealed class ParseState {
            public readonly List<string> Positionals = new List<string>();
            public readonly Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.Ordinal);
            public bool OptionsEnded;
            public bool HelpRequested;
        }

        private static readonly Dictionary<string, CliCommandKind> _globalOptions = new Dictionary<string, CliCommandKind>(StringComparer.Ordinal) {
            { "--help", CliCommandKind.Help },
            { "-h", CliCommandKind.Help },
            { "--version", CliCommandKind.Version }
        };

        private static readonly Dictionary<string, CommandSpec> _commands = new Dictionary<string, CommandSpec>(StringComparer.Ordinal) {
            { "list", new CommandSpec { Kind = CliCommandKind.List, Usage = "ctw list", PositionalCount = 0, Options = new string[0] } },
            { "add", new CommandSpec { Kind = CliCommandKind.Add, Usage = "ctw add <url> [--description <text>] [--category <text>]", PositionalCount = 1,
                Options = new[] { DescriptionOption, CategoryOption } } },
            { "remove", new CommandSpec { Kind = CliCommandKind.Remove, Usage = "ctw remove <url>", PositionalCount = 1, Options = new string[0] } },
            { "watch", new CommandSpec { Kind = CliCommandKind.Watch, Usage = "ctw watch", PositionalCount = 0, Options = new string[0] } }
        };

        // Returns false with a one-line error for a command line that is not valid
        public static bool TryParse(string[] args, out CliCommand command, out string error) {
            command = null;
            error = null;
            if (args.Length == 0) {
                command = new CliCommand { Kind = CliCommandKind.MissingCommand };
                return true;
            }
            CliCommandKind globalKind;
            if (_globalOptions.TryGetValue(args[0], out globalKind)) return TryParseGlobalOption(args, globalKind, out command, out error);
            CommandSpec spec;
            if (_commands.TryGetValue(args[0], out spec)) return TryParseCommand(args, spec, out command, out error);
            error = "Unknown command '" + ConsoleText.Clean(args[0]) + "'.";
            return false;
        }

        private static bool TryParseGlobalOption(string[] args, CliCommandKind kind, out CliCommand command, out string error) {
            command = null;
            error = null;
            if (args.Length > 1) {
                error = "'" + args[0] + "' takes no other arguments.";
                return false;
            }
            command = new CliCommand { Kind = kind };
            return true;
        }

        private static bool TryParseCommand(string[] args, CommandSpec spec, out CliCommand command, out string error) {
            command = null;
            ParseState state = new ParseState();
            for (int i = 1; i < args.Length; i++) {
                if (!TryReadArgument(args, ref i, spec, state, out error)) return false;
            }
            error = null;
            if (state.HelpRequested) {
                command = new CliCommand { Kind = CliCommandKind.CommandHelp, HelpText = spec.Usage };
                return true;
            }
            if (state.Positionals.Count != spec.PositionalCount) {
                error = "Wrong arguments. Usage: " + spec.Usage;
                return false;
            }
            command = CreateCommand(spec.Kind, state);
            return true;
        }

        // Reads args[i], and for an option also its value (i is then moved past it)
        private static bool TryReadArgument(string[] args, ref int i, CommandSpec spec, ParseState state, out string error) {
            error = null;
            string arg = args[i];
            if (state.OptionsEnded || !IsOption(arg)) {
                state.Positionals.Add(arg);
                return true;
            }
            if (arg == EndOfOptions) {
                state.OptionsEnded = true;
                return true;
            }
            if (IsHelp(arg)) {
                state.HelpRequested = true;
                return true;
            }
            return TryReadOption(args, ref i, spec, state, out error);
        }

        private static bool TryReadOption(string[] args, ref int i, CommandSpec spec, ParseState state, out string error) {
            error = null;
            string option = args[i];
            if (Array.IndexOf(spec.Options, option) == -1) {
                error = "Unknown option '" + ConsoleText.Clean(option) + "'. Usage: " + spec.Usage;
                return false;
            }
            if (i + 1 >= args.Length || args[i + 1].StartsWith(EndOfOptions, StringComparison.Ordinal)) {
                error = "Option '" + option + "' needs a value (one that does not start with \"--\").";
                return false;
            }
            if (!state.Options.TryAdd(option, args[++i])) error = "Option '" + option + "' is given more than once.";
            return error == null;
        }

        // A URL never starts with "-"
        private static bool IsOption(string arg) {
            return arg.Length > 1 && arg[0] == '-';
        }

        private static bool IsHelp(string arg) {
            return arg == "--help" || arg == "-h";
        }

        private static CliCommand CreateCommand(CliCommandKind kind, ParseState state) {
            return new CliCommand {
                Kind = kind,
                Url = state.Positionals.Count != 0 ? state.Positionals[0] : null,
                Description = state.Options.GetValueOrDefault(DescriptionOption),
                Category = state.Options.GetValueOrDefault(CategoryOption)
            };
        }
    }
}
