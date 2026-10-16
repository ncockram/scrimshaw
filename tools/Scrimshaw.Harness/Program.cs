// Scrimshaw developer harness.
//   scrimshaw-harness replay <framesDir> [--labels labels.json] [--threshold 0.03] [--tol 24]
//                            [--settle 1] [--settle-tol 8] [--settle-area 0.01] [--no-longest] [--verbose]
//   scrimshaw-harness synth <outDir> [--style cut|half|fade|slide|mixed] [--pages 8] [--hold 5] [--noise 4] [--seed 1]
//   scrimshaw-harness bench [--width 3840] [--height 2160] [--runs 20] [--interval 0.5]
//   scrimshaw-harness record <outDir> --window "title text" [--interval 1] [--max 900]
//   scrimshaw-harness contact <framesDir> <out.png> [--from 1] [--to 9999] [--step 1] [--cols 8] [--width 240]
//   scrimshaw-harness render-pdf <file.pdf> [outDir] [dpi]
using Scrimshaw.Harness;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: scrimshaw-harness replay <framesDir> [options] | synth <outDir> [options] | bench [options] | record <outDir> --window <text> [options] | contact <framesDir> <out.png> [options] | render-pdf <file.pdf> [outDir] [dpi]");
    return 2;
}

var options = new CommandLine(args.Skip(1).ToArray());
return args[0] switch
{
    "replay" => ReplayCommand.Run(options),
    "synth" => SynthCommand.Run(options),
    "bench" => BenchCommand.Run(options),
    "record" => await RecordCommand.RunAsync(options),
    "contact" => ContactCommand.Run(options),
    "render-pdf" => await RenderPdfCommand.RunAsync(options),
    _ => Fail($"unknown command '{args[0]}'"),
};

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
