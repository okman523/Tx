﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CSharp;

namespace Tx.Windows
{
    public class TmfParser
    {
        private static readonly Regex _header = new Regex(@"^(\S*) (\S*) //.*SRC=(\S+)");
        private static readonly Regex _field = new Regex("^(.*), (\\S*)");

        // TMF files from Lync contain extra token, compared to TMF-s from SCOM
        private static readonly Regex _eventHeader1 =
            new Regex("^#typev\\s+(\\S*)\\s+(\\d*)\\s+\"(.*)\"\\s+//.*FUNC=(\\S*)");

        private static readonly Regex _eventHeader2 =
            new Regex("^#typev\\s+(\\S*)\\s+(\\S*)\\s+(\\d*)\\s+\"(.*)\"\\s+//.*FUNC=(\\S*)");

        private static readonly CSharpCodeProvider _provider = new CSharpCodeProvider();
        private static readonly SortedSet<string> _namespacesEmitted = new SortedSet<string>();

        private static readonly Dictionary<string, string> _typeMap = new Dictionary<string, string>
            {
                {"ItemChar", "win:UInt8"},
                {"ItemUChar", "win:UInt8"},
                {"ItemCharShort", "win:UInt8"},
                {"ItemCharSign", "win:UInt8"},
                {"ItemShort", "win:Int16"},
                {"ItemUShort", "win:UInt16"},
                {"ItemLong", "win:Int32"},
                {"ItemULong", "win:UInt32"},
                {"ItemULongX", "win:UInt32"},
                {"ItemLongLong", "win:Int64"},
                {"ItemULongLong", "win:UInt64"},
                {"ItemLongLongX", "win:UInt64"},
                {"ItemLongLongXX", "win:UInt64"},
                {"ItemLongLongO", "win:UInt64"},
                {"ItemString", "win:AnsiString"},
                {"ItemWString", "win:UnicodeString"},
                {"ItemRString", "win:AnsiString"},
                {"ItemRWString", "win:UnicodeString"},
                {"ItemPString", "win:AnsiStringPref"},
                {"ItemPWString", "win:UnicodeStringPref"},
                {"ItemPII", "win:UnicodeString"},
                {"ItemDSString", "win:AnsiStringPref"},
                {"ItemDSWString", "win:UnicodeStringPref"},
                {"ItemSid", "win:SID"},
                {"ItemChar4", null},
                {"ItemIPAddr", "win:UInt32"},
                {"ItemIPV6Addr", null},
                {"ItemMACAddr", "win:GUID"},
                {"ItemPort", "win:UInt16"},
                {"ItemMLString", "win:AnsiStringPref"},
                {"ItemNWString", "win:UnicodeString"},
                {"ItemPtr", "win:UInt64"},
                {"ItemListLong", "win:Int32"},
                {"ItemListShort", "win:Int16"},
                {"ItemListwin:UInt8", "win:Int8"},
                {"ItemNTerror", null},
                {"ItemMerror", null},
                {"ItemTimestamp", "win:UInt64"},
                {"ItemTimeStamp", "win:UInt64"}, // Wow do we really see both?
                {"ItemGuid", "win:GUID"},
                {"ItemNTSTATUS", "win:UInt16"},
                {"ItemWINERROR", "win:UInt16"},
                {"ItemNETEVENT", null},
                {"ItemWaitTime", null},
                {"ItemTimeDelta", null},
                {"ItemSetLong", null},
                {"ItemSetShort", null},
                {"ItemSetwin:UInt8", null},
                {"ItemDouble", "win:Double"},
                {"ItemHRESULT", "win:UInt16"},
                {"ItemCharHidden", "win:UInt8"},
                {"ItemWChar", "win.UInt16"},
                {"ItemHexDump", null},
                {"ItemEventLog", null},
                {"ItemSRB", null},
                {"ItemSenseData", null},
                {"ItemEnum", "win:Int32"},
                {"ItemResource", null},
                {"ItemCLSID", "win:GUID"},
                {"ItemIID", "win:GUID"},
                {"ItemLIBID", "win:GUID"},
                {"ItemSockAddr", null},
                {"ItemKSid", null},
                {"ItemCWString", null},
                {"ItemNStrings", null},
                {"ItemFQDN", "win:UnicodeString"},
                {"ItemURI", "win:UnicodeString"},
                {"ItemURIums", "win:UnicodeString"},
                {"ItemE164ums", "win:UnicodeString"},
                {"ItemIP", "win:UnicodeString"},
                {"ItemHOST", "win:UnicodeString"},
                {"ItemListLong(false,true)", "win:UnicodeString"},
            };

        private readonly string _code;
        private readonly string _componentName;
        private readonly string _providerId;

        private readonly TextReader _reader;
        private readonly StringBuilder _sb;
        private readonly string _srcfile;
        private int _classCounter;
        private SortedSet<string> _classesEmitted;
        private SortedSet<string> _fieldsEmitted;
        private string _function;

        private TmfParser(string tmfFile)
        {
            _reader = File.OpenText(tmfFile);
            _reader.ReadLine(); //PDB:  e:\lcsrgs.obj.amd64chk\rgs\dev\...\microsoft.rtc.rgs.clients.pdb
            _reader.ReadLine(); //PDB:  Last Updated :2012-5-4:12:51:1:778 (UTC) [ManagedWPP]

            string line = _reader.ReadLine();

            Match m = _header.Match(line);
            if (!m.Success)
                throw new Exception("failed to match the header line in file " + tmfFile);
            // the expected format is like
            // f73bbb29-e2f0-93e7-ee22-e396f8fe1570 RgsClientsLib // SRC=matchmakinglocator.cs MJ= MN=

            _providerId = m.Groups[1].Value;
            _componentName = m.Groups[2].Value;
            _srcfile = m.Groups[3].Value
                                  .Replace('-', '_')
                                  .Replace('.', '_');

            _sb = new StringBuilder(
                @"// 
//    This code was generated by EtwEventTypeGen.exe 
//

using System;

");
            _sb.Append("namespace Microsoft.Etw.");
            _sb.Append(_provider.CreateValidIdentifier(_componentName));
            _sb.Append('.');
            _sb.AppendLine(_srcfile.Replace('.', '_'));
            _sb.AppendLine("{");

            while (true)
            {
                if (!ReadEvent())
                    break;
            }
            _sb.AppendLine("    }");
            _sb.AppendLine("}");

            _code = _sb.ToString();
        }

        public string Code
        {
            get { return _code; }
        }

        public static string Parse(string tmfFile)
        {
            var p = new TmfParser(tmfFile);
            return p.Code;
        }

        private bool ReadEvent()
        {
            string fileLine;
            string lineNumber;
            string opcode;
            string function;

            string line = _reader.ReadLine();
            if (line == null)
                return false;

            while (line.StartsWith("#enumv"))
            {
                ReadEnum();
                line = _reader.ReadLine();

                if (line == null)
                    return false;
            }

            if (line.StartsWith("// PDB"))
                return false;
                    // looks like some TMFs contain the same information over and over again from different PDBs 

            while (true)
            {
                string more = _reader.ReadLine();
                if (more == "{")
                    break;

                line += " " + more;
            }

            Match m = _eventHeader1.Match(line);
            if (m.Success)
            {
                fileLine = m.Groups[1].Value;
                lineNumber = fileLine.Substring(_srcfile.Length);
                opcode = m.Groups[2].Value;
                function = CreateIdentifier(m.Groups[4].Value);

                EmitClass(function, lineNumber, opcode);
            }
            else
            {
                m = _eventHeader2.Match(line);
                if (!m.Success)
                    throw new Exception("the TMF format does not match one of the two supported formats");

                fileLine = m.Groups[1].Value;
                lineNumber = fileLine.Substring(_srcfile.Length);
                opcode = m.Groups[3].Value;
                function = CreateIdentifier(m.Groups[5].Value);

                EmitClass(function, lineNumber, opcode);
            }

            return true;
        }

        private void EmitClass(string function, string lineNumber, string opcode)
        {
            EmitNamespaceIfChanged(function);

            if (_classCounter++ > 0)
                _sb.AppendLine();

            _sb.AppendFormat("        [ManifestEvent(\"{0}\", {1})]", _providerId, opcode);
            _sb.AppendLine();
            _sb.Append("        class Line");
            _sb.AppendLine(CreateUniqueClassName(lineNumber));
            _sb.AppendLine("        {");

            int fieldIndex = 0;
            _fieldsEmitted = new SortedSet<string>();
            while (true)
            {
                string line = _reader.ReadLine();
                if (line == "}")
                    break;

                EmitField(line, fieldIndex++);
            }

            _sb.AppendLine("        }");
        }

        private void EmitNamespaceIfChanged(string function)
        {
            if (function != _function)
            {
                if (_function != null)
                {
                    _sb.AppendLine("    }");
                    _sb.AppendLine();
                }

                _function = CreateUniquNamespaceName(function);
                _sb.Append("    namespace ");
                _sb.AppendLine(_function);
                _sb.AppendLine("    {");
                _classCounter = 0;
                _classesEmitted = new SortedSet<string>();
            }
        }

        private void EmitField(string line, int fieldIndex)
        {
            Match m = _field.Match(line);
            if (!m.Success)
                throw new Exception("field definition does not match expected format: " + line);

            string name = CreateUniqieFieldName(m.Groups[1].Value, fieldIndex);
            string type = m.Groups[2].Value;

            if (fieldIndex > 0)
                _sb.AppendLine();

            string manifestType;
            if (!_typeMap.TryGetValue(type, out manifestType) || manifestType == null)
            {
                manifestType = "win:UnicodeString";
            }
            _sb.AppendFormat("            [EventField(0, {0}, \"{1}\")]", fieldIndex, manifestType);
            _sb.AppendLine();
            string cShapType = ManifestParser.CleanType(manifestType);
            _sb.AppendFormat("            public {0} {1}", cShapType, name);
            _sb.AppendLine(" { get; set; }");
        }

        private string CreateIdentifier(string s)
        {
            char[] chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!Char.IsLetterOrDigit(chars[i]))
                    chars[i] = ' ';
            }

            string name = new string(chars).Replace(" ", "");
            if (name.Length > 0 && !Char.IsLetter(name[0]))
                name = "_" + name;

            return _provider.CreateValidIdentifier(name);
        }

        private string CreateUniqueClassName(string name)
        {
            string uniqueName = name;
            int counter = 1;
            while (_classesEmitted.Contains(uniqueName))
            {
                uniqueName = name + "_" + counter.ToString(CultureInfo.InvariantCulture);
                counter++;
            }
            _classesEmitted.Add(uniqueName);

            return uniqueName;
        }

        private string CreateUniquNamespaceName(string name)
        {
            string uniqueName = name;
            int counter = 1;
            while (_namespacesEmitted.Contains(uniqueName))
            {
                uniqueName = name + "_" + counter.ToString(CultureInfo.InvariantCulture);
                counter++;
            }
            _namespacesEmitted.Add(uniqueName);

            return uniqueName;
        }

        private string CreateUniqieFieldName(string s, int fieldIndex)
        {
            string name = CreateIdentifier(s);
            if (name == "")
                return "field" + fieldIndex.ToString(CultureInfo.InvariantCulture);

            if (name.Length > 30)
                name = name.Substring(0, 30);

            string uniqueName = name;
            int counter = 1;
            while (_fieldsEmitted.Contains(uniqueName))
            {
                uniqueName = name + counter.ToString(CultureInfo.InvariantCulture);
                counter++;
            }
            _fieldsEmitted.Add(uniqueName);

            return uniqueName;
        }

        private void ReadEnum()
        {
            string line;
            do
            {
                line = _reader.ReadLine();
            } while (line != "}");
        }
    }
}