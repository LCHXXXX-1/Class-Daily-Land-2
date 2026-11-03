// 显式列出全局 using：契约文件（Contract\*.cs）在主工程里依赖主工程的隐式 using，
// 这里不赌 SDK 默认值，直接钉死，两边的编译环境就完全一致。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
