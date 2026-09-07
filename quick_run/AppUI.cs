using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using QuickCmd;

namespace quick_run
{
    // qchost（quick command host，quick command 的常驻托盘宿主进程）入口：
    // 单实例互斥后跑 TrayContext（托盘菜单 + 实例跟踪 + IPC 服务端）
    internal class AppUI
    {
            public static int Run()
            {
                bool created;
                // 注：互斥体名中的 "gui" 指 qchost，属存量协议名不改，避免新旧进程互不识别
                using (var mutex = new Mutex(true, "Local\\quickrun.gui", out created))
                {
                    if (!created)
                    {
                        return 0;
                    }
                    using (var ctx = new TrayContext()) Application.Run(ctx);
                    return 0;
                }
            }
    }
}
