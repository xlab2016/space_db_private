using Magic.Kernel.Compilation;
using Magic.Kernel.Processor;
using System;
using System.Linq;

var source = @"@AGI 0.0.1;

program test;
module test;

procedure Main {
    var stream1 := stream<file>;
    stream1.open(""stream1"");
}

entrypoint {
    Main;
}";

var parser = new Parser();
var semanticAnalyzer = new SemanticAnalyzer();
var structure = parser.ParseProgram(source);
var analyzed = semanticAnalyzer.AnalyzeProgram(structure, parser, source);

var asm = analyzed.Procedures["Main"].Body;
Console.WriteLine($"Instructions in Main: {asm.Count}");
for (int i = 0; i < asm.Count; i++)
{
    var cmd = asm[i];
    var op1 = cmd.Operand1?.GetType().Name + ": " + cmd.Operand1;
    Console.WriteLine($"  [{i}] Opcode={cmd.Opcode}, Op1={op1}");
}

// Find CallObj
var callObjIdx = asm.FindIndex(c => c.Opcode == Opcodes.CallObj && (c.Operand1 as string) == "open");
Console.WriteLine($"\nCallObj 'open' at index: {callObjIdx}");
if (callObjIdx >= 0)
{
    Console.WriteLine($"  openCallIndex-2: [{callObjIdx-2}] = {asm[callObjIdx-2].Opcode}, {asm[callObjIdx-2].Operand1?.GetType().Name}: {asm[callObjIdx-2].Operand1}");
    Console.WriteLine($"  openCallIndex-1: [{callObjIdx-1}] = {asm[callObjIdx-1].Opcode}, {asm[callObjIdx-1].Operand1?.GetType().Name}: {asm[callObjIdx-1].Operand1}");
}
