// Reference assembly 12: IL that the C# compiler never produces.
// fault handlers, filters with non-trivial bodies, jmp, tail. calls, a switch whose fall-through is a case,
// and exception regions nested inside handlers.

.assembly extern System.Runtime
{
  .publickeytoken = (B0 3F 5F 7F 11 D5 0A 3A)
  .ver 8:0:0:0
}
.assembly Ref12.HandIL
{
  .ver 1:0:0:0
}
.module Ref12.HandIL.dll

.class public auto ansi abstract sealed beforefieldinit Ref.HandIL.Constructs
       extends [System.Runtime]System.Object
{
  .field public static int32 Cleanups

  // try { body } fault { Cleanups++ }  — the handler runs only when an exception leaves the try.
  .method public hidebysig static int32 WithFault(int32 x) cil managed
  {
    .maxstack 2
    .locals init (int32 V_0)
    .try
    {
      ldarg.0
      ldc.i4.0
      bge.s POSITIVE
      newobj instance void [System.Runtime]System.ArgumentOutOfRangeException::.ctor()
      throw
    POSITIVE:
      ldarg.0
      ldc.i4.2
      mul
      stloc.0
      leave.s DONE
    }
    fault
    {
      ldsfld int32 Ref.HandIL.Constructs::Cleanups
      ldc.i4.1
      add
      stsfld int32 Ref.HandIL.Constructs::Cleanups
      endfault
    }
  DONE:
    ldloc.0
    ret
  }

  // A filter that inspects the exception, with a branch inside the filter block.
  .method public hidebysig static int32 WithFilter(string s) cil managed
  {
    .maxstack 2
    .locals init (int32 V_0)
    .try
    {
      ldarg.0
      call int32 [System.Runtime]System.Int32::Parse(string)
      stloc.0
      leave.s DONE
    }
    filter
    {
      isinst [System.Runtime]System.FormatException
      dup
      brtrue.s IS_FORMAT
      pop
      ldc.i4.0
      br.s DECIDE
    IS_FORMAT:
      pop
      ldarg.0
      callvirt instance int32 [System.Runtime]System.String::get_Length()
      ldc.i4.0
      cgt
    DECIDE:
      endfilter
    }
    {
      pop
      ldc.i4.m1
      stloc.0
      leave.s DONE
    }
  DONE:
    ldloc.0
    ret
  }

  // Exception regions nested inside a handler: try/catch inside a finally, and try/fault inside a catch.
  .method public hidebysig static int32 NestedInHandlers(int32 x) cil managed
  {
    .maxstack 2
    .locals init (int32 V_0)
    .try
    {
      .try
      {
        ldarg.0
        ldc.i4.0
        div
        stloc.0
        leave.s INNER_DONE
      }
      catch [System.Runtime]System.DivideByZeroException
      {
        pop
        .try
        {
          ldc.i4.7
          stloc.0
          leave.s CATCH_DONE
        }
        fault
        {
          ldc.i4.0
          stloc.0
          endfault
        }
      CATCH_DONE:
        leave.s INNER_DONE
      }
    INNER_DONE:
      leave.s DONE
    }
    finally
    {
      .try
      {
        ldsfld int32 Ref.HandIL.Constructs::Cleanups
        ldc.i4.1
        add
        stsfld int32 Ref.HandIL.Constructs::Cleanups
        leave.s FIN_DONE
      }
      catch [System.Runtime]System.Object
      {
        pop
        leave.s FIN_DONE
      }
    FIN_DONE:
      endfinally
    }
  DONE:
    ldloc.0
    ret
  }

  // jmp transfers to a method with the same signature, passing the current arguments.
  .method public hidebysig static int32 JumpTarget(int32 x) cil managed
  {
    .maxstack 2
    ldarg.0
    ldc.i4.s 10
    add
    ret
  }

  .method public hidebysig static int32 Jumper(int32 x) cil managed
  {
    .maxstack 8
    jmp int32 Ref.HandIL.Constructs::JumpTarget(int32)
  }

  // tail.-prefixed recursive call (a loop in IL terms).
  .method public hidebysig static int64 Factorial(int32 n, int64 acc) cil managed
  {
    .maxstack 3
    ldarg.0
    ldc.i4.1
    bgt.s RECURSE
    ldarg.1
    ret
  RECURSE:
    ldarg.0
    ldc.i4.1
    sub
    ldarg.1
    ldarg.0
    conv.i8
    mul
    tail.
    call int64 Ref.HandIL.Constructs::Factorial(int32, int64)
    ret
  }

  // switch without a default: an out-of-range value falls through into the first case's code.
  .method public hidebysig static string SwitchFallThrough(int32 x) cil managed
  {
    .maxstack 1
    ldarg.0
    switch (CASE0, CASE1, CASE2, CASE1)
  CASE0:
    ldstr "zero or out of range"
    ret
  CASE1:
    ldstr "one or three"
    ret
  CASE2:
    ldstr "two"
    ret
  }

  // A loop whose body is a switch; the back edge comes from two places.
  .method public hidebysig static int32 SwitchLoop(int32[] ops) cil managed
  {
    .maxstack 3
    .locals init (int32 acc, int32 i)
    ldc.i4.0
    stloc.0
    ldc.i4.0
    stloc.1
    br.s TEST
  BODY:
    ldarg.0
    ldloc.1
    ldelem.i4
    switch (INC, DBL, NEXT)
    ldloc.0
    ldc.i4.1
    sub
    stloc.0
    br.s NEXT
  INC:
    ldloc.0
    ldc.i4.1
    add
    stloc.0
    br.s NEXT
  DBL:
    ldloc.0
    ldc.i4.2
    mul
    stloc.0
  NEXT:
    ldloc.1
    ldc.i4.1
    add
    stloc.1
  TEST:
    ldloc.1
    ldarg.0
    ldlen
    conv.i4
    blt.s BODY
    ldloc.0
    ret
  }

  // Unreachable code after an unconditional branch, and an explicit rethrow in a catch.
  .method public hidebysig static void Rethrows() cil managed
  {
    .maxstack 1
    .try
    {
      newobj instance void [System.Runtime]System.InvalidOperationException::.ctor()
      throw
    }
    catch [System.Runtime]System.InvalidOperationException
    {
      pop
      rethrow
    }
    br.s END
    nop
  END:
    ret
  }

  // calli through a function pointer loaded with ldftn.
  .method public hidebysig static int32 IndirectCall(int32 x) cil managed
  {
    .maxstack 2
    ldarg.0
    ldftn int32 Ref.HandIL.Constructs::JumpTarget(int32)
    calli int32(int32)
    ret
  }
}
