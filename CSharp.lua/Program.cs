/*
Copyright 2016 YANG Huan (sy.yanghuan@gmail.com).
Copyright 2016 Redmoon Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

  http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CSharpLua {
  class Program {
    private const string HelpCmdString = @"Usage: CSharp.lua [-s srcfolder] [-d dstfolder]
Arguments 
-s              : source directory, all *.cs files whill be compiled
-d              : destination  directory, will put the out lua files

Options
-h              : show the help message    
-l              : libraries referenced, use ';' to separate      
-m              : meta files, like System.xml, use ';' to separate     
-csc            : csc.exe command argumnets, use ';' to separate

-c              : support classic lua version(5.1), default support 5.3 
-i              : indent number, default is 2
-sem            : append semicolon when statement over
-a              : attributes need to export, use ';' to separate, if ""-a"" only, all attributes whill be exported    
";
    public static void Main(string[] args) {
      if (args.Length > 0) {
        try {
          var cmds = Utility.GetCommondLines(args);
          if (cmds.ContainsKey("-h")) {
            ShowHelpInfo();
            return;
          }

          Console.WriteLine($"start {DateTime.Now}");

          string folder = cmds.GetArgument("-s");
          string output = cmds.GetArgument("-d");
          string lib = cmds.GetArgument("-l", true);
          string meta = cmds.GetArgument("-m", true);
          string csc = cmds.GetArgument("-csc", true);
          bool isClassic = cmds.ContainsKey("-c");
          string indent = cmds.GetArgument("-i", true);
          bool hasSemicolon = cmds.ContainsKey("-sem");
          string atts = cmds.GetArgument("-a", true);
          if (atts == null && cmds.ContainsKey("-a")) {
            atts = string.Empty;
          }
          Worker w = new Worker(folder, output, lib, meta, csc, isClassic, indent, hasSemicolon, atts);
          w.Do();
          Console.WriteLine("all operator success");
          Console.WriteLine($"end {DateTime.Now}");
        }
        catch (CmdArgumentException e) {
          Console.Error.WriteLine(e.Message);
          ShowHelpInfo();
          Environment.ExitCode = -1;
        }
        catch (CompilationErrorException e) {
          Console.Error.WriteLine(e.Message);
          Environment.ExitCode = -1;
        }
        catch (Exception e) {
          Console.Error.WriteLine(e.ToString());
          Environment.ExitCode = -1;
        }
      }
      else {
        ShowHelpInfo();
        Environment.ExitCode = -1;
      }
    }

    private static void ShowHelpInfo() {
      Console.Error.WriteLine(HelpCmdString);
    }
  }
}
