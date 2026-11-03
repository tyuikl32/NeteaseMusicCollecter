using System;
using System.Collections.Generic;
using System.Text;

namespace NeteaseMusicCollecter.Interfaces
{
    public interface ICollectorParser
    {
        /// <summary>
        /// 解析一行已解码的日志。与本行无关时返回 <c>null</c>。
        /// </summary>
        CollectorUpdate? Parse(LogLine line);
    }
}
