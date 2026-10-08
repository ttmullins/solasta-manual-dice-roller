// Optional developer grammar check using official PowerShell7.4.6 parser.
// Parsing does not execute a script or certify Windows PowerShell5.1 behavior.
using System;
using System.IO;
using System.Management.Automation.Language;
using System.Security.Cryptography;

static class AuditPowerShellSyntax
{
    static int Main(string[] args)
    {
        int errors=0;
        Console.WriteLine("Official parser assembly: "+typeof(Parser).Assembly.FullName);
        foreach(var file in args) {
            Token[] tokens;ParseError[] failures;
            Parser.ParseFile(file,out tokens,out failures);
            errors+=failures.Length;
            string hash;
            using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-","").ToLowerInvariant();
            Console.WriteLine(Path.GetFileName(file)+" SHA256 "+hash+"; "+failures.Length+" parse errors");
            foreach(var failure in failures) Console.WriteLine(failure.ToString());
        }
        Console.WriteLine("PowerShell grammar check: "+errors+" errors; no script execution or actual Windows validation.");
        return errors==0?0:1;
    }
}
