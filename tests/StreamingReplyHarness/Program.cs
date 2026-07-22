using System;
using System.Collections.Generic;
using LilithMod;
using Newtonsoft.Json.Linq;

var parser = new StreamingReplyParser();
var found = new List<JObject>();
string[] fragments =
{
    "{\"li", "nes\":[{\"spoken\":\"Mm... {stay}\",", "\"shown\":\"Mm... stay\"},",
    "{\"spoken\":\"Second\\\" line\",\"shown\":\"Second line\"}]",
    ",\"action\":{\"type\":\"timer\",\"seconds\":5}}"
};
foreach (string fragment in fragments) found.AddRange(parser.Append(fragment));

if (found.Count != 2 ||
    (string)found[0]["spoken"] != "Mm... {stay}" ||
    (string)found[1]["spoken"] != "Second\" line" ||
    !parser.Content.Contains("\"action\""))
{
    Console.Error.WriteLine("STREAMING HARNESS FAIL");
    Environment.Exit(1);
}

Console.WriteLine("STREAMING HARNESS PASS");
