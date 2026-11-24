// Pec.Qc.NavisRunner: opens a COPY of a Navisworks model in a separate hidden Navisworks, runs the PecQcExporter plugin and closes it again.
// The model the user has open, and the original file, are never touched and nothing is saved.
//   Pec.Qc.NavisRunner.exe --navis "<Navisworks folder>" --model "<model.nwd>" --out "<snapshot.json>" [--plugin "<PecQcExporter.dll>"]
// Exit code: 0 done | 2 wrong arguments | 3 Navisworks missing or the model could not be opened | 4 the export failed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PecQc.Runner
{
    internal static class Program
    {
        private static string _navis;

        private static int Main(string[] args)
        {
            Dictionary<string, string> a = Parse(args);
            string navis, model, outPath;
            if (!a.TryGetValue("navis", out navis) || !a.TryGetValue("model", out model) || !a.TryGetValue("out", out outPath))
            {
                Console.Error.WriteLine("ERROR usage: Pec.Qc.NavisRunner.exe --navis <Navisworks folder> --model <model.nwd> --out <snapshot.json> [--plugin <PecQcExporter.dll>]");
                return 2;
            }
            string plugin;
            if (!a.TryGetValue("plugin", out plugin)) plugin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PecQcExporter.dll");
            if (!File.Exists(Path.Combine(navis, "Autodesk.Navisworks.Automation.dll"))) { Console.Error.WriteLine("ERROR Navisworks was not found in " + navis); return 3; }
            if (!File.Exists(model)) { Console.Error.WriteLine("ERROR the model file does not exist: " + model); return 3; }
            if (!File.Exists(plugin)) { Console.Error.WriteLine("ERROR the exporter plugin is missing: " + plugin); return 4; }

            _navis = navis;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;   // Navisworks DLLs come from the user's own installation
            string tmp = Path.Combine(Path.GetTempPath(), "pecqc_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tmp);
                string copy = Path.Combine(tmp, "model.nwd");
                Console.WriteLine("PROGRESS copying the model");
                File.Copy(model, copy);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                return Export(copy, plugin, outPath);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR " + e.GetType().Name + ": " + e.Message);
                return 4;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        // a separate method so no Navisworks type is loaded before the resolver is registered
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Export(string copy, string plugin, string outPath)
        {
            Autodesk.Navisworks.Api.Automation.NavisworksApplication app = null;
            try
            {
                Console.WriteLine("PROGRESS starting Navisworks (hidden)");
                app = new Autodesk.Navisworks.Api.Automation.NavisworksApplication();
                app.Visible = false;
                Console.WriteLine("PROGRESS opening the model");
                app.OpenFile(copy);
                app.AddPluginAssembly(plugin);
                Console.WriteLine("PROGRESS reading pipes, branches and components");
                int rc = app.ExecuteAddInPlugin("PecQcExporter.PECQ", outPath);
                if (rc != 0 || !File.Exists(outPath)) { Console.Error.WriteLine("ERROR the exporter plugin failed (code " + rc + ")"); return 4; }
                Console.WriteLine("OK " + outPath);
                return 0;
            }
            finally
            {
                if (app != null) { try { app.Dispose(); } catch { } }
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            if (!name.StartsWith("Autodesk.Navisworks", StringComparison.OrdinalIgnoreCase)) return null;
            string file = Path.Combine(_navis, name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < args.Length; i += 2)
                if (args[i].StartsWith("--")) d[args[i].Substring(2)] = args[i + 1];
            return d;
        }
    }
}
