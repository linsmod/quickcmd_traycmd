$code = @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class K {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateJobObjectW(IntPtr a, string n);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool QueryInformationJobObject(IntPtr h, int c, IntPtr buf, uint cb, out uint ret);
  [DllImport("kernel32.dll", SetLastError=true, EntryPoint="#793")] public static extern bool GJI_K32(IntPtr h, int c, IntPtr buf, uint cb);
  [DllImport("kernelbase.dll", SetLastError=true, EntryPoint="#793")] public static extern bool GJI_KB(IntPtr h, int c, IntPtr buf, uint cb);
  public static string Run() {
    IntPtr a = CreateJobObjectW(IntPtr.Zero, null);
    IntPtr b = CreateJobObjectW(IntPtr.Zero, null);
    var sb = new StringBuilder();
    foreach (var t in new string[]{"K32","KB"}) {
      foreach (uint cb in new uint[]{24,48}) {
        for (int cls=0; cls<=4; cls++) {
          uint va = Call(t, a, cls, cb), vb = Call(t, b, cls, cb);
          sb.Append(t + " cb=" + cb + " cls=" + cls + " a=" + Hex(va) + " b=" + Hex(vb) + (va==vb?" SAME":" DIFF") + "\n");
        }
      }
    }
    return sb.ToString();
  }
  static string Hex(uint v) { return v==0xFFFFFFFFu?"ERR":v.ToString("x8"); }
  static uint Call(string t, IntPtr h, int cls, uint cb) {
    IntPtr buf = Marshal.AllocHGlobal(64);
    for (int i=0;i<64;i++) Marshal.WriteByte(buf, i, 0xFF);
    bool ok = t=="K32" ? GJI_K32(h, cls, buf, cb) : GJI_KB(h, cls, buf, cb);
    if (!ok) return 0xFFFFFFFF;
    return (uint)Marshal.ReadInt32(buf, 4);   // 读 offset 4（候选 KMTID 位置）
  }
}
"@
Add-Type -TypeDefinition $code -Language CSharp
Write-Output ([K]::Run())
