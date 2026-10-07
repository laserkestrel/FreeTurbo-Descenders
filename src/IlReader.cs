#nullable disable   // also compiled into the bridge (nullable enabled); the mod has nullable off
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Lists the methods a method body calls, in IL order, by decoding its opcodes (sizes from System.Reflection.Emit.OpCodes).
    /// Used at runtime to confirm what a game method calls (StartGate.cs); pure .NET, also compiled into the
    /// bridge for `selftest`.
    /// </summary>
    public static class IlReader
    {
        private static OpCode[] _one, _two;

        /// <summary>Metadata tokens of every call / callvirt / newobj operand in <paramref name="il"/>, in order. Stops at anything it cannot decode.</summary>
        public static List<int> CallTokens(byte[] il)
        {
            if (_one == null) BuildTables();
            var tokens = new List<int>();
            if (il == null) return tokens;
            int i = 0;
            while (i < il.Length)
            {
                OpCode op;
                if (il[i] == 0xFE)
                {
                    if (i + 1 >= il.Length) break;
                    op = _two[il[i + 1]]; i += 2;
                }
                else { op = _one[il[i]]; i += 1; }
                if (op.Size == 0) break;   // unknown opcode: stop rather than read garbage
                int operand = OperandSize(op.OperandType, il, i);
                if (operand < 0 || i + operand > il.Length) break;
                if ((op.Value == OpCodes.Call.Value || op.Value == OpCodes.Callvirt.Value || op.Value == OpCodes.Newobj.Value) && operand == 4)
                    tokens.Add(il[i] | (il[i + 1] << 8) | (il[i + 2] << 16) | (il[i + 3] << 24));
                i += operand;
            }
            return tokens;
        }

        /// <summary>The methods <paramref name="m"/> calls, in order; tokens that do not resolve (fields, generic context) come back null.</summary>
        public static List<MethodBase> Calls(MethodBase m)
        {
            var list = new List<MethodBase>();
            MethodBody body = m.GetMethodBody();
            if (body == null) return list;
            foreach (int tok in CallTokens(body.GetILAsByteArray()))
            {
                MethodBase callee = null;
                try { callee = m.Module.ResolveMethod(tok); } catch { }
                list.Add(callee);
            }
            return list;
        }

        private static int OperandSize(OperandType t, byte[] il, int at)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (at + 4 > il.Length) return -1;
                    int n = il[at] | (il[at + 1] << 8) | (il[at + 2] << 16) | (il[at + 3] << 24);
                    return n < 0 || n > 100000 ? -1 : 4 + 4 * n;
                default: return 4;   // InlineBrTarget, InlineField, InlineI, InlineMethod, InlineSig, InlineString, InlineTok, InlineType, ShortInlineR
            }
        }

        private static void BuildTables()
        {
            var one = new OpCode[256];
            var two = new OpCode[256];
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object v = f.GetValue(null);
                if (!(v is OpCode)) continue;
                OpCode op = (OpCode)v;
                ushort value = unchecked((ushort)op.Value);
                if (op.Size == 1) one[value & 0xFF] = op;
                else if ((value >> 8) == 0xFE) two[value & 0xFF] = op;
            }
            _two = two; _one = one;
        }
    }
}
