// PEC QC exporter plugin. Runs inside a hidden Navisworks (started by Pec.Qc.NavisRunner) and writes a JSON snapshot of every PIPE > BRAN > component with its AVEVA
// attributes (the NWD is an AVEVA E3D export). READ-ONLY: it never modifies or saves the model. The JSON shape is read by Pec.Qc.Core.Model.ModelSnapshot.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;

namespace PecQc
{
    [Plugin("PecQcExporter", "PECQ", DisplayName = "PEC QC Exporter")]
    [AddInPlugin(AddInLocation.None)]
    public class Exporter : AddInPlugin
    {
        public const int SchemaVersion = 1;

        static PropertyCategory Aveva(ModelItem it)
        {
            return it.PropertyCategories.FindCategoryByDisplayName("AVEVA");
        }

        static string TypeOf(ModelItem it)
        {
            PropertyCategory av = Aveva(it);
            if (av == null) return null;
            DataProperty p = av.Properties.FindPropertyByDisplayName("TYPE");
            if (p == null) return null;
            try { return p.Value.ToDisplayString(); } catch { return null; }
        }

        // all non-empty AVEVA attributes as name -> display string ("?" means empty in Navisworks)
        static Dictionary<string, string> Attrs(ModelItem it)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            PropertyCategory av = Aveva(it);
            if (av == null) return d;
            foreach (DataProperty p in av.Properties)
            {
                string v;
                try { v = p.Value.ToDisplayString(); } catch { continue; }
                if (string.IsNullOrEmpty(v) || v == "?") continue;
                d[p.DisplayName] = v;
            }
            return d;
        }

        static Dictionary<string, object> Node(ModelItem it, string type)
        {
            Dictionary<string, object> n = new Dictionary<string, object>();
            n["name"] = it.DisplayName;
            n["type"] = type;
            n["attrs"] = Attrs(it);
            return n;
        }

        public override int Execute(params string[] parameters)
        {
            string outPath = parameters[0];
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            List<object> lines = new List<object>();
            List<object> nozzles = new List<object>();
            int items = 0;
            string source = "";
            foreach (Model m in doc.Models)
            {
                source = m.SourceFileName;
                foreach (ModelItem it in m.RootItem.DescendantsAndSelf)
                {
                    items++;
                    string t = TypeOf(it);
                    if (t == "NOZZ") { nozzles.Add(Node(it, t)); continue; }
                    if (t != "PIPE") continue;

                    Dictionary<string, object> line = Node(it, t);
                    // where it sits in the Navisworks tree (to find duplicated or misplaced pipes)
                    List<string> path = new List<string>();
                    foreach (ModelItem a in it.Ancestors) { path.Add(a.DisplayName); }
                    path.Reverse();
                    line["path"] = string.Join(" > ", path.ToArray());
                    List<object> branches = new List<object>();
                    foreach (ModelItem b in it.Children)
                    {
                        string bt = TypeOf(b);
                        if (bt != "BRAN") continue;
                        Dictionary<string, object> br = Node(b, bt);
                        List<object> comps = new List<object>();
                        foreach (ModelItem c in b.Children)   // children are in order along the branch
                        {
                            string ct = TypeOf(c);
                            if (ct == null) continue;
                            comps.Add(Node(c, ct));
                        }
                        br["items"] = comps;
                        branches.Add(br);
                    }
                    line["branches"] = branches;
                    lines.Add(line);
                }
            }

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["schema"] = SchemaVersion;
            root["exported"] = DateTime.Now.ToString("s");
            root["navisworks"] = typeof(Document).Assembly.GetName().Version.ToString();
            root["exporter"] = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            root["source"] = source;
            root["totalItems"] = items;
            root["lines"] = lines;
            root["nozzles"] = nozzles;

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            // written to a temporary name and renamed, so a reader never sees half a file
            string tmp = outPath + ".part";
            File.WriteAllText(tmp, js.Serialize(root), new System.Text.UTF8Encoding(false));
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);
            return 0;
        }
    }
}
