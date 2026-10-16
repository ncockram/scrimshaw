// Scrimshaw developer harness.
//   scrimshaw-harness replay <framesDir> [--labels labels.json] [--threshold 0.03] [--tol 24]
//                            [--settle 1] [--settle-tol 8] [--settle-area 0.01] [--verbose]
//   scrimshaw-harness synth <outDir> [--style cut|half|fade|slide|mixed] [--pages 8] [--hold 5] [--noise 4] [--seed 1]
//   scrimshaw-harness render-pdf <file.pdf> [outDir] [dpi]
using Scrimshaw.Harness;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: scrimshaw-harness replay <framesDir> [options] | synth <outDir> [options] | render-pdf <file.pdf> [outDir] [dpi]");
    return 2;
}

var options = new CommandLine(args.Skip(1).ToArray());
return args[0] switch
{
    "replay" => ReplayCommand.Run(options),
    "synth" => SynthCommand.Run(options),
    "render-pdf" => await RenderPdfCommand.RunAsync(options),
    _ => Fail($"unknown command '{args[0]}'"),
};

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
