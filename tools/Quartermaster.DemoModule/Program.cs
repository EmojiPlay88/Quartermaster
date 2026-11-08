using Quartermaster.DemoModule;

var (vmod, rules) = DemoContent.Build(args.Length > 0 ? args[0] : ".");
Console.WriteLine(vmod);
Console.WriteLine(rules);
