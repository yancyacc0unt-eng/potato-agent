// ============================================================================
// 文件工具（PotatoAgent.Files）自测 —— 可复跑工程，退出码 0 = 全部通过。
//
// 覆盖九块：
//   一、路径解析    ：绝对原样 / 相对拼工作区 / 没工作区就报错 / 规范化（. 与 ..）
//   二、file_list   ：目录在前再按名字、类型/大小/时间、pattern、recursive、limit 截断、各种错误路径
//   三、file_write  ：新建无 BOM、覆盖保留 BOM、UTF-8 中文往返、CRLF 不翻译、append、建目录、只读文件失败不崩
//   四、file_read   ：总字节/总行数、offset+limit 切片、max_bytes 截断标记、CRLF/LF、二进制拒绝、
//                     目录当文件读、不存在、空文件、offset 越界
//   五、file_copy   ：拷进目录沿用原名、指定全路径、冲突、overwrite、目录树报错、父目录不存在
//   六、file_move   ：缺父目录自动建、新旧路径回报、移进目录、冲突、overwrite、目录报错、改名
//   七、file_delete ：回收站（原路径消失）、重复删、目录 recursive 门禁、路径解析错误
//   八、契约        ：六个名字 / 六个 Risk 等级 / schema 是合法 JSON object / 参数齐全 / Description 全英文
//   九、注册        ：RegisterAll 数量、重名抛异常、skipExisting 跳过、导出 OpenAI tools 数组、工作区回调实时
//
// ⚠ 安全前提：所有读写都在 %TEMP%\potato-files-selftest-<随机> 里，跑完递归删干净，
//   绝不读、绝不写 %APPDATA%\PotatoAgent，也绝不碰用户的任何真实文件。
//   唯一会离开沙箱的副作用：七、里把 1 个文件 + 1 个文件夹送进了回收站（这是 file_delete 的正常行为，
//   它们只是从磁盘挪到回收站，用户可以从回收站还原、或直接清空回收站）。
//
// 跑法：
//   dotnet build build\FilesSelfTest.csproj -c Debug
//   dotnet run   --project build\FilesSelfTest.csproj -c Debug
// ============================================================================

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PotatoAgent.Core.Tools;
using PotatoAgent.Files;

namespace PotatoAgent.Files.SelfTest;

internal static class Program
{
    private static int _pass;
    private static int _fail;

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private static int Main()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 输出被重定向时可能失败，不影响结论 */ }

        var sandbox = Path.Combine(Path.GetTempPath(), "potato-files-selftest-" + Guid.NewGuid().ToString("N"));

        Console.WriteLine("==================== PotatoAgent.Files 文件工具自测 ====================");
        Console.WriteLine($"临时沙箱 : {sandbox}");
        Console.WriteLine("落盘范围 : 只在沙箱目录里；只有第七节的 file_delete 会把 1 个文件 + 1 个文件夹送进回收站");
        Console.WriteLine();

        try
        {
            Directory.CreateDirectory(sandbox);

            PathRules(sandbox);
            ListTool(sandbox);
            WriteTool(sandbox);
            ReadTool(sandbox);
            CopyTool(sandbox);
            MoveTool(sandbox);
            DeleteTool(sandbox);
            Contract(sandbox);
            Registration(sandbox);
        }
        catch (Exception error)
        {
            Fail($"自测自身抛异常（说明被测代码漏了一种异常）: {error.GetType().Name}: {error.Message}");
        }
        finally
        {
            Cleanup(sandbox);

            Section("护栏");
            Check("沙箱目录跑完已删干净", !Directory.Exists(sandbox));
            Console.WriteLine();
        }

        return Report();
    }

    // ==================== 一、路径解析 ====================

    private static void PathRules(string sandbox)
    {
        Section("一、路径解析");

        var dir = Path.Combine(sandbox, "paths");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "hello", NoBom);

        Check("绝对路径：解析结果就是它自己",
            FilePaths.TryResolve(file, null, out var abs, out var e1) && Same(abs, file) && e1 is null);
        Check("绝对路径带尾分隔符：被规范化掉",
            FilePaths.TryResolve(dir + Path.DirectorySeparatorChar, null, out var d1, out _) && Same(d1, dir));
        Check("相对路径：拼到工作区根下",
            FilePaths.TryResolve(@"paths\a.txt", Workspace(sandbox), out var rel, out _) && Same(rel, file));
        Check("相对路径 \".\"：就是工作区根",
            FilePaths.TryResolve(".", Workspace(sandbox), out var dot, out _) && Same(dot, sandbox));
        Check("相对路径里的 .. 也被规范化",
            FilePaths.TryResolve(@"paths\..\paths\a.txt", Workspace(sandbox), out var up, out _) && Same(up, file));

        var ok = FilePaths.TryResolve(@"paths\a.txt", null, out _, out var error);
        Check("没有工作区时相对路径：失败", !ok && error is not null);
        Check("错误是英文人话且以约定句子开头",
            error is not null && error.StartsWith("Relative paths need an open workspace", StringComparison.Ordinal));
        Check("回调返回 null：也算没有工作区",
            !FilePaths.TryResolve("a.txt", () => null, out _, out var e2) && e2 is not null);
        Check("回调返回空白：也算没有工作区", !FilePaths.TryResolve("a.txt", () => "   ", out _, out _));
        Check("回调自己抛异常：不炸，按没有工作区处理", !FilePaths.TryResolve("a.txt", () => throw new InvalidOperationException("boom"), out _, out _));
        Check("空路径：报错", !FilePaths.TryResolve("", Workspace(sandbox), out _, out _));
        Check("null 路径：报错", !FilePaths.TryResolve(null, Workspace(sandbox), out _, out _));

        var read = CallTool("file_read", Json(("path", @"paths\a.txt")), Workspace(sandbox));
        Check("工具用相对路径也能找到文件", read.Success);
        Check("工具回报的是解析后的绝对路径", Mentions(read, file));
    }

    // ==================== 二、file_list ====================

    private static void ListTool(string sandbox)
    {
        Section("二、file_list");

        var dir = Path.Combine(sandbox, "list");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "zFolder"));
        Directory.CreateDirectory(Path.Combine(dir, "aFolder"));
        File.WriteAllText(Path.Combine(dir, "b.cs"), "bbb", NoBom);
        File.WriteAllText(Path.Combine(dir, "a.md"), "aaaa", NoBom);
        File.WriteAllText(Path.Combine(dir, "c.txt"), "cc", NoBom);
        var sub = Path.Combine(dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "deep.cs"), "deep", NoBom);

        var all = CallTool("file_list", Json(("path", dir)), null);
        Check("列目录成功", all.Success);
        Check("回报解析后的绝对路径", Mentions(all, dir));
        Check("每行的格式是 类型 + 大小 + 修改时间 + 名字",
            Regex.IsMatch(all.Content, @"\[file\]\s+4\s+\d{4}-\d{2}-\d{2} \d{2}:\d{2}\s+a\.md"));
        Check("目录行标 [dir ]、文件行标 [file]", all.Content.Contains("[dir ]") && all.Content.Contains("[file]"));

        var ia = all.Content.IndexOf("aFolder", StringComparison.Ordinal);
        var iz = all.Content.IndexOf("zFolder", StringComparison.Ordinal);
        var ib = all.Content.IndexOf("b.cs", StringComparison.Ordinal);
        var imd = all.Content.IndexOf("a.md", StringComparison.Ordinal);
        Check("目录排在文件前面", ia >= 0 && ib >= 0 && ia < ib);
        Check("同类里按名字排序（aFolder 在 zFolder 之前）", ia >= 0 && iz > ia);
        Check("同类里按名字排序（a.md 在 b.cs 之前）", imd >= 0 && ib > imd);

        var filtered = CallTool("file_list", Json(("path", dir), ("pattern", "*.cs")), null);
        Check("pattern=*.cs：只留 .cs，且不递归时不进子目录",
            filtered.Success && filtered.Content.Contains("b.cs") &&
            !filtered.Content.Contains("a.md") && !filtered.Content.Contains("deep.cs"));

        var recursive = CallTool("file_list", Json(("path", dir), ("pattern", "*.cs"), ("recursive", true)), null);
        Check("recursive=true：找到子目录里的 deep.cs", recursive.Success && recursive.Content.Contains("deep.cs"));
        Check("recursive 时名字一列显示相对路径", recursive.Content.Contains(Path.Combine("sub", "deep.cs")));

        var many = Path.Combine(sandbox, "many");
        Directory.CreateDirectory(many);
        for (var i = 0; i < 25; i++)
        {
            File.WriteAllText(Path.Combine(many, $"f{i:D2}.txt"), "x", NoBom);
        }

        var limited = CallTool("file_list", Json(("path", many), ("limit", 10)), null);
        Check("limit=10：正好列 10 条", Regex.Matches(limited.Content, @"\[file\]").Count == 10);
        Check("被截断时明说还剩 15 条没列", limited.Content.Contains("15 more are not shown"));
        Check("limit 超上限（99999）被夹住而不是报错",
            CallTool("file_list", Json(("path", many), ("limit", 99999)), null).Success);

        var missing = CallTool("file_list", Json(("path", Path.Combine(sandbox, "no-such-folder"))), null);
        Check("目录不存在：失败且说 does not exist", !missing.Success && missing.Content.Contains("does not exist"));
        var asFile = CallTool("file_list", Json(("path", Path.Combine(dir, "b.cs"))), null);
        Check("把文件当目录列：失败且说 is a file", !asFile.Success && asFile.Content.Contains("is a file"));
        var badPattern = CallTool("file_list", Json(("path", dir), ("pattern", @"src\*.cs")), null);
        Check("pattern 带路径分隔符：明确报错",
            !badPattern.Success && badPattern.Content.Contains("must not contain a path separator"));
        Check("相对路径 + 没有工作区：失败",
            !CallTool("file_list", Json(("path", "list")), null).Success);
    }

    // ==================== 三、file_write ====================

    private static void WriteTool(string sandbox)
    {
        Section("三、file_write");

        var dir = Path.Combine(sandbox, "write");
        var target = Path.Combine(dir, "new.txt");
        const string Content = "hello 中文\nsecond\n";

        var created = CallTool("file_write", Json(("path", target), ("content", Content)), null);
        Check("新建成功（父目录默认自动建）", created.Success && Directory.Exists(dir));
        Check("回报写入字节数（20 字节）", created.Content.Contains("Wrote 20 bytes"));
        Check("回报写完后的文件大小", created.Content.Contains("now 20 bytes"));

        var bytes = File.ReadAllBytes(target);
        Check("新文件 UTF-8 无 BOM", bytes.Length == 20 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
        Check("中文 UTF-8 往返逐字节一致", bytes.SequenceEqual(Encoding.UTF8.GetBytes(Content)));
        Check("内容原样落盘（没有换行翻译）", Encoding.UTF8.GetString(bytes) == Content);

        var bomFile = Path.Combine(dir, "bom.txt");
        File.WriteAllBytes(bomFile, Utf8Bom.Concat(Encoding.UTF8.GetBytes("old\n")).ToArray());
        var overwritten = CallTool("file_write", Json(("path", bomFile), ("content", "new 内容\n")), null);
        var bomBytes = File.ReadAllBytes(bomFile);
        Check("覆盖已存在的文件成功", overwritten.Success);
        Check("覆盖后保留原来的 UTF-8 BOM",
            bomBytes.Length == 3 + Encoding.UTF8.GetByteCount("new 内容\n") &&
            bomBytes[0] == 0xEF && bomBytes[1] == 0xBB && bomBytes[2] == 0xBF);
        Check("覆盖后内容 = BOM + 新内容",
            bomBytes.SequenceEqual(Utf8Bom.Concat(Encoding.UTF8.GetBytes("new 内容\n")).ToArray()));
        Check("文本里说明了保留 BOM", overwritten.Content.Contains("keeping the existing BOM"));

        var appended = CallTool("file_write", Json(("path", target), ("mode", "append"), ("content", "tail\n")), null);
        Check("append 成功", appended.Success);
        Check("append 后内容 = 原文 + 新文",
            File.ReadAllText(target, Encoding.UTF8) == Content + "tail\n");
        Check("append 不会重复写 BOM",
            File.ReadAllBytes(target).Length == 25 && File.ReadAllBytes(target)[0] != 0xEF);

        var bomAppend = CallTool("file_write", Json(("path", bomFile), ("mode", "append"), ("content", "tail\n")), null);
        Check("给带 BOM 的文件追加：BOM 仍然只有一个且在最前",
            bomAppend.Success && File.ReadAllBytes(bomFile)
                .SequenceEqual(Utf8Bom.Concat(Encoding.UTF8.GetBytes("new 内容\ntail\n")).ToArray()));

        var empty = Path.Combine(dir, "empty.txt");
        CallTool("file_write", Json(("path", empty), ("content", "abc")), null);
        var truncated = CallTool("file_write", Json(("path", empty), ("content", "")), null);
        Check("content 可以是空串（文件被截成 0 字节）", truncated.Success && new FileInfo(empty).Length == 0);

        var crlf = Path.Combine(dir, "crlf.txt");
        var crlfWrite = CallTool("file_write", Json(("path", crlf), ("content", "a\r\nb\r\n")), null);
        Check("CRLF 原样落盘（没被翻译成 LF）",
            crlfWrite.Success && File.ReadAllBytes(crlf).SequenceEqual(Encoding.UTF8.GetBytes("a\r\nb\r\n")));

        var deep = Path.Combine(sandbox, "no", "such", "dir", "x.txt");
        var noFolders = CallTool("file_write", Json(("path", deep), ("content", "x"), ("create_directories", false)), null);
        Check("create_directories=false 且父目录不存在：失败",
            !noFolders.Success && noFolders.Content.Contains("does not exist"));
        Check("失败后没有偷偷建目录", !Directory.Exists(Path.GetDirectoryName(deep)));
        Check("create_directories=true 时同样的路径能写成功",
            CallTool("file_write", Json(("path", deep), ("content", "x")), null).Success && File.Exists(deep));

        var readOnly = Path.Combine(dir, "readonly.txt");
        File.WriteAllText(readOnly, "locked", NoBom);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var denied = CallTool("file_write", Json(("path", readOnly), ("content", "nope")), null);
        Check("写只读文件：失败但没崩（不是异常）",
            !denied.Success && !denied.Content.StartsWith("THREW", StringComparison.Ordinal));
        Check("写只读文件失败后内容没被改", File.ReadAllText(readOnly) == "locked");
        File.SetAttributes(readOnly, FileAttributes.Normal);

        Check("缺 content：失败", !CallTool("file_write", Json(("path", target)), null).Success);
        Check("mode 写错：失败并说明合法取值",
            !CallTool("file_write", Json(("path", target), ("content", "x"), ("mode", "banana")), null).Success);
        var asDir = CallTool("file_write", Json(("path", dir), ("content", "x")), null);
        Check("路径是目录：失败且说 is a folder", !asDir.Success && asDir.Content.Contains("is a folder"));
    }

    // ==================== 四、file_read ====================

    private static void ReadTool(string sandbox)
    {
        Section("四、file_read");

        var dir = Path.Combine(sandbox, "read");
        Directory.CreateDirectory(dir);

        var lines = Path.Combine(dir, "lines.txt");
        File.WriteAllText(lines, string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line{i:D3}")) + "\n", NoBom);

        var all = CallTool("file_read", Json(("path", lines)), null);
        Check("整读成功", all.Success);
        Check("回报总字节数", all.Content.Contains($"{N(new FileInfo(lines).Length)} bytes"));
        Check("回报总行数（300 行）", all.Content.Contains("300 line(s)"));
        Check("回报换行风格 LF", all.Content.Contains("Line endings: LF"));
        Check("默认一次读完并说明区间", all.Content.Contains("Showing lines 1-300 of 300"));
        Check("行号是 1 基且右对齐", all.Content.Contains("   1| line001"));
        Check("末行内容正确", all.Content.Contains(" 300| line300"));

        var slice = CallTool("file_read", Json(("path", lines), ("offset", 10), ("limit", 5)), null);
        Check("切片：写明显示第 10-14 行", slice.Content.Contains("Showing lines 10-14 of 300"));
        Check("切片首行正确", slice.Content.Contains("  10| line010"));
        Check("切片末行正确", slice.Content.Contains("  14| line014"));
        Check("切片外的一行都没多给", !slice.Content.Contains("line015") && !slice.Content.Contains("line009"));
        Check("截断标记写明还剩 286 行", slice.Content.Contains("286 more line(s) are not shown"));
        Check("截断标记给出下一步 offset=15", slice.Content.Contains("offset=15"));
        Check("说明了前面 1-9 行为什么不显示", slice.Content.Contains("lines 1-9 are not shown"));

        var byBytes = CallTool("file_read", Json(("path", lines), ("max_bytes", 20)), null);
        Check("max_bytes 截断：明确写 TRUNCATED by 'max_bytes'", byBytes.Content.Contains("TRUNCATED by 'max_bytes'"));
        Check("max_bytes 截断：给出继续读的 offset", byBytes.Content.Contains("Call file_read again with offset="));
        Check("max_bytes=20 时只显示前两行（每行 8 字节）", byBytes.Content.Contains("Showing lines 1-2 of 300"));

        var past = CallTool("file_read", Json(("path", lines), ("offset", 9999)), null);
        Check("offset 越界：不算失败，但明说没有内容可给",
            past.Success && past.Content.Contains("past the end"));

        var crlf = Path.Combine(dir, "crlf.txt");
        File.WriteAllBytes(crlf, Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma\r\n"));
        var crlfRead = CallTool("file_read", Json(("path", crlf)), null);
        Check("CRLF 文件能读且认作 3 行", crlfRead.Success && crlfRead.Content.Contains("3 line(s)"));
        Check("报出换行风格是 CRLF", crlfRead.Content.Contains("Line endings: CRLF"));
        Check("CRLF 的三行都被正确切开",
            crlfRead.Content.Contains("   1| alpha") && crlfRead.Content.Contains("   2| beta") &&
            crlfRead.Content.Contains("   3| gamma"));

        var binary = Path.Combine(dir, "blob.bin");
        File.WriteAllBytes(binary, new byte[] { 0x41, 0x42, 0x00, 0x43, 0x44 });
        var binaryRead = CallTool("file_read", Json(("path", binary)), null);
        Check("前 8 KB 有 NUL：当二进制拒绝", !binaryRead.Success);
        Check("二进制错误里说 binary，且不吐文件内容",
            binaryRead.Content.Contains("binary") && !binaryRead.Content.Contains("AB"));

        var asDir = CallTool("file_read", Json(("path", dir)), null);
        Check("目录当文件读：失败且说 is a folder", !asDir.Success && asDir.Content.Contains("is a folder"));

        var missing = CallTool("file_read", Json(("path", Path.Combine(dir, "nope.txt"))), null);
        Check("不存在的文件：失败且给出绝对路径",
            !missing.Success && missing.Content.Contains("No such file") && missing.Content.Contains(dir));

        var emptyFile = Path.Combine(dir, "empty.txt");
        File.WriteAllBytes(emptyFile, Array.Empty<byte>());
        var emptyRead = CallTool("file_read", Json(("path", emptyFile)), null);
        Check("空文件：成功且说明是空的", emptyRead.Success && emptyRead.Content.Contains("empty"));

        Check("缺 path：失败", !CallTool("file_read", "{}", null).Success);
    }

    // ==================== 五、file_copy ====================

    private static void CopyTool(string sandbox)
    {
        Section("五、file_copy");

        var src = Path.Combine(sandbox, "copy-src");
        var dst = Path.Combine(sandbox, "copy-dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var a = Path.Combine(src, "a.txt");
        File.WriteAllText(a, "copy me", NoBom);

        var intoFolder = CallTool("file_copy", Json(("source", a), ("destination", dst)), null);
        var into = Path.Combine(dst, "a.txt");
        Check("目标是已存在目录：拷进去并沿用原名", intoFolder.Success && File.Exists(into));
        Check("拷过去的内容一致", File.ReadAllText(into) == "copy me");
        Check("原文件还在", File.Exists(a));
        Check("回报里同时有源和目标的绝对路径", Mentions(intoFolder, a) && Mentions(intoFolder, into));

        var full = Path.Combine(dst, "b.txt");
        Check("指定完整目标路径可用",
            CallTool("file_copy", Json(("source", a), ("destination", full)), null).Success && File.Exists(full));

        File.WriteAllText(full, "keep me", NoBom);
        var conflict = CallTool("file_copy", Json(("source", a), ("destination", full)), null);
        Check("目标已存在且 overwrite=false：失败", !conflict.Success);
        Check("冲突失败时目标文件一个字节没动", File.ReadAllText(full) == "keep me");
        Check("冲突失败时告知要用 overwrite", conflict.Content.Contains("overwrite=true"));

        var overwrite = CallTool("file_copy", Json(("source", a), ("destination", full), ("overwrite", true)), null);
        Check("overwrite=true：覆盖成功", overwrite.Success && File.ReadAllText(full) == "copy me");

        var tree = CallTool("file_copy", Json(("source", src), ("destination", dst)), null);
        Check("源是目录：明确报错（本轮不做目录树拷贝）",
            !tree.Success && tree.Content.Contains("not supported yet"));

        var orphan = CallTool("file_copy",
            Json(("source", a), ("destination", Path.Combine(sandbox, "ghost", "x.txt"))), null);
        Check("目标父目录不存在：失败并说明不建目录",
            !orphan.Success && orphan.Content.Contains("does not exist"));

        var noSource = CallTool("file_copy", Json(("source", Path.Combine(src, "nope.txt")), ("destination", dst)), null);
        Check("源不存在：失败", !noSource.Success);

        var self = CallTool("file_copy", Json(("source", a), ("destination", a)), null);
        Check("源和目标同一个文件：失败且什么都没做", !self.Success && File.ReadAllText(a) == "copy me");

        var relative = CallTool("file_copy",
            Json(("source", @"copy-src\a.txt"), ("destination", @"copy-dst\rel.txt")), Workspace(sandbox));
        Check("两边都用相对路径（靠工作区解析）",
            relative.Success && File.Exists(Path.Combine(dst, "rel.txt")));
    }

    // ==================== 六、file_move ====================

    private static void MoveTool(string sandbox)
    {
        Section("六、file_move");

        var src = Path.Combine(sandbox, "move-src");
        var dst = Path.Combine(sandbox, "move-dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        var a = Path.Combine(src, "a.txt");
        File.WriteAllText(a, "move me", NoBom);

        var deepTarget = Path.Combine(dst, "made", "up", "folders", "a.txt");
        var moved = CallTool("file_move", Json(("source", a), ("destination", deepTarget)), null);
        Check("缺父目录：自动建出来并移动成功", moved.Success && File.Exists(deepTarget));
        Check("旧路径消失", !File.Exists(a));
        Check("回报里新旧两个绝对路径都在", Mentions(moved, a) && Mentions(moved, deepTarget));
        Check("内容原样搬过去", File.ReadAllText(deepTarget) == "move me");
        Check("文本里说明建了目录", moved.Content.Contains("Created missing folder"));

        var intoFolder = CallTool("file_move", Json(("source", deepTarget), ("destination", dst)), null);
        var inner = Path.Combine(dst, "a.txt");
        Check("目标是已存在目录：移进去沿用原名",
            intoFolder.Success && File.Exists(inner) && !File.Exists(deepTarget));

        var other = Path.Combine(src, "b.txt");
        File.WriteAllText(other, "other", NoBom);
        var conflict = CallTool("file_move", Json(("source", other), ("destination", inner)), null);
        Check("目标已存在且 overwrite=false：失败", !conflict.Success);
        Check("冲突失败时源还在、目标没被改", File.Exists(other) && File.ReadAllText(inner) == "move me");

        var overwrite = CallTool("file_move",
            Json(("source", other), ("destination", inner), ("overwrite", true)), null);
        Check("overwrite=true：覆盖成功且源消失",
            overwrite.Success && File.ReadAllText(inner) == "other" && !File.Exists(other));

        var dirMove = CallTool("file_move", Json(("source", src), ("destination", dst)), null);
        Check("源是目录：明确报错（本轮不搬目录）", !dirMove.Success && dirMove.Content.Contains("not supported yet"));

        Check("源不存在：失败",
            !CallTool("file_move", Json(("source", Path.Combine(src, "nope.txt")), ("destination", dst)), null).Success);

        var renameSource = Path.Combine(src, "c.txt");
        var renameTarget = Path.Combine(src, "renamed.txt");
        File.WriteAllText(renameSource, "c", NoBom);
        var renamed = CallTool("file_move", Json(("source", renameSource), ("destination", renameTarget)), null);
        Check("同目录改名可用", renamed.Success && File.Exists(renameTarget) && !File.Exists(renameSource));
    }

    // ==================== 七、file_delete（回收站） ====================

    private static void DeleteTool(string sandbox)
    {
        Section("七、file_delete（只走回收站）");

        var dir = Path.Combine(sandbox, "delete");
        Directory.CreateDirectory(dir);

        var victim = Path.Combine(dir, "victim.txt");
        File.WriteAllText(victim, "bye", NoBom);

        var recycled = CallTool("file_delete", Json(("path", victim)), null);
        Check("回收文件：返回成功", recycled.Success);
        Check("回收后原路径消失", !File.Exists(victim));
        Check("没抛异常（拿到的是结果对象）",
            !recycled.Content.StartsWith("THREW", StringComparison.Ordinal));
        Check("文本里说明进了回收站", recycled.Content.Contains("Recycle Bin"));
        Check("回报解析后的绝对路径", Mentions(recycled, victim));

        var again = CallTool("file_delete", Json(("path", victim)), null);
        Check("再删同一个路径：失败但不抛异常",
            !again.Success && !again.Content.StartsWith("THREW", StringComparison.Ordinal));

        var tree = Path.Combine(dir, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "inner.txt"), "inner", NoBom);

        var noRecursive = CallTool("file_delete", Json(("path", tree)), null);
        Check("目录 + recursive=false：失败并要求显式 recursive=true",
            !noRecursive.Success && noRecursive.Content.Contains("recursive=true"));
        Check("被拒之后目录和里面的文件都还在",
            Directory.Exists(tree) && File.Exists(Path.Combine(tree, "inner.txt")));

        var recursive = CallTool("file_delete", Json(("path", tree), ("recursive", true)), null);
        Check("目录 + recursive=true：整棵树进回收站", recursive.Success && !Directory.Exists(tree));
        Check("树里的文件也一起走了", !File.Exists(Path.Combine(tree, "inner.txt")));

        Check("缺 path：失败", !CallTool("file_delete", "{}", null).Success);

        var noWorkspace = CallTool("file_delete", Json(("path", @"delete\victim2.txt")), null);
        Check("没有工作区时的相对路径：失败且没有动任何东西",
            !noWorkspace.Success && noWorkspace.Content.Contains("Relative paths need an open workspace"));
    }

    // ==================== 八、契约（名字 / 等级 / schema / 描述） ====================

    private static void Contract(string sandbox)
    {
        Section("八、工具契约");

        var tools = FileTools.CreateAll(Workspace(sandbox));
        Check("CreateAll 返回 6 个工具", tools.Count == 6);
        Check("名字正好是约定的六个",
            string.Join(",", tools.Select(t => t.Name)) == "file_list,file_read,file_write,file_copy,file_move,file_delete");

        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Check("file_list  = Safe", byName["file_list"].Risk == ToolRisk.Safe);
        Check("file_read  = Safe", byName["file_read"].Risk == ToolRisk.Safe);
        Check("file_write = Confirm", byName["file_write"].Risk == ToolRisk.Confirm);
        Check("file_copy  = Confirm", byName["file_copy"].Risk == ToolRisk.Confirm);
        Check("file_move  = Confirm", byName["file_move"].Risk == ToolRisk.Confirm);
        Check("file_delete = Dangerous", byName["file_delete"].Risk == ToolRisk.Dangerous);

        Check("六个 ParametersJsonSchema 都是合法 JSON object", tools.All(IsJsonObject));
        Check("Description 都是够长的纯英文（模型靠它选工具）",
            tools.All(t => t.Description.Length > 60 && t.Description.All(c => c < 0x7F)));

        Check("file_list 参数齐（path/pattern/recursive/limit）且都不必填",
            Properties(byName["file_list"], "path", "pattern", "recursive", "limit") &&
            Required(byName["file_list"]).Length == 0);
        Check("file_read 参数齐（path/offset/limit/max_bytes）且只必填 path",
            Properties(byName["file_read"], "path", "offset", "limit", "max_bytes") &&
            Required(byName["file_read"]) is ["path"]);
        Check("file_write 参数齐（path/content/mode/create_directories）且必填 path+content",
            Properties(byName["file_write"], "path", "content", "mode", "create_directories") &&
            Required(byName["file_write"]).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "content", "path" }));
        Check("file_copy 参数齐（source/destination/overwrite）且必填 source+destination",
            Properties(byName["file_copy"], "source", "destination", "overwrite") &&
            Required(byName["file_copy"]).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "destination", "source" }));
        Check("file_move 参数齐（source/destination/overwrite）且必填 source+destination",
            Properties(byName["file_move"], "source", "destination", "overwrite") &&
            Required(byName["file_move"]).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "destination", "source" }));
        Check("file_delete 参数齐（path/recursive）且只必填 path",
            Properties(byName["file_delete"], "path", "recursive") &&
            Required(byName["file_delete"]) is ["path"]);
    }

    // ==================== 九、注册（RegisterAll / CreateAll） ====================

    private static void Registration(string sandbox)
    {
        Section("九、注册与工作区回调");

        var registry = new ToolRegistry();
        var registered = FileTools.RegisterAll(registry, Workspace(sandbox));
        Check("RegisterAll 首次注册 6 个", registered == 6 && registry.Count == 6);

        var exported = false;
        try
        {
            using var doc = JsonDocument.Parse(registry.BuildToolsJson());
            exported = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() == 6;
        }
        catch (JsonException)
        {
            exported = false;
        }

        Check("注册后能导出成 OpenAI tools 数组（6 项）", exported);
        Check("注册表能按名查到 file_delete", registry.TryGet("file_delete", out var found) && found is not null);

        var threw = false;
        try
        {
            FileTools.RegisterAll(registry, Workspace(sandbox));
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Check("skipExisting=false 重名会抛 ArgumentException", threw);
        Check("抛了之后注册表还是 6 个", registry.Count == 6);

        var skipped = FileTools.RegisterAll(registry, Workspace(sandbox), skipExisting: true);
        Check("skipExisting=true 全部跳过并返回 0", skipped == 0 && registry.Count == 6);

        var fresh = new ToolRegistry();
        var intoFresh = FileTools.RegisterAll(fresh, null, skipExisting: true);
        Check("空注册表 + skipExisting=true：照样注册 6 个", intoFresh == 6 && fresh.Count == 6);

        var nullRegistry = false;
        try
        {
            FileTools.RegisterAll(null!);
        }
        catch (ArgumentNullException)
        {
            nullRegistry = true;
        }

        Check("registry 传 null：抛 ArgumentNullException", nullRegistry);
        Check("CreateAll() 不带工作区也能建出 6 个", FileTools.CreateAll().Count == 6);
        Check("CreateAll 每次返回独立实例（互不共享状态）",
            !ReferenceEquals(FileTools.CreateAll()[0], FileTools.CreateAll()[0]));

        // 工作区回调必须是"每次调用重新问一次"，而不是建工具时问一次。
        var rootOne = Path.Combine(sandbox, "ws-one");
        var rootTwo = Path.Combine(sandbox, "ws-two");
        Directory.CreateDirectory(rootOne);
        Directory.CreateDirectory(rootTwo);
        File.WriteAllText(Path.Combine(rootOne, "t.txt"), "root-one", NoBom);
        File.WriteAllText(Path.Combine(rootTwo, "t.txt"), "root-two", NoBom);

        string? current = rootOne;
        var live = FileTools.CreateAll(() => current).First(t => t.Name == "file_read");
        var first = Call(live, Json(("path", "t.txt")));
        current = rootTwo;
        var second = Call(live, Json(("path", "t.txt")));
        current = null;
        var third = Call(live, Json(("path", "t.txt")));

        Check("换工作区后同一次构造的工具读到新根的文件",
            first.Success && first.Content.Contains("root-one") &&
            second.Success && second.Content.Contains("root-two"));
        Check("工作区被关掉后相对路径立刻报错",
            !third.Success && third.Content.Contains("Relative paths need an open workspace"));
    }

    // ==================== 断言、工具调用与汇总 ====================

    private static ToolResult CallTool(string name, string argsJson, Func<string?>? workspace) =>
        Call(FileTools.CreateAll(workspace).First(t => t.Name == name), argsJson);

    /// <summary>调用工具，并把"工具自己抛异常"也转成失败结果 —— 自测要能区分"返回失败"和"炸了"。</summary>
    private static ToolResult Call(ITool tool, string argsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            return tool.InvokeAsync(doc.RootElement, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"THREW {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>拼一个参数 JSON（自己拼会把反斜杠写坏，这里统一走 JsonSerializer）。</summary>
    private static string Json(params (string Key, object? Value)[] pairs)
    {
        var text = new StringBuilder("{");
        foreach (var (key, value) in pairs)
        {
            if (text.Length > 1)
            {
                text.Append(',');
            }

            text.Append(JsonSerializer.Serialize(key)).Append(':');
            text.Append(value switch
            {
                null => "null",
                bool flag => flag ? "true" : "false",
                int number => number.ToString(CultureInfo.InvariantCulture),
                long number => number.ToString(CultureInfo.InvariantCulture),
                _ => JsonSerializer.Serialize(value),
            });
        }

        return text.Append('}').ToString();
    }

    private static bool IsJsonObject(ITool tool)
    {
        try
        {
            using var doc = JsonDocument.Parse(tool.ParametersJsonSchema);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Properties(ITool tool, params string[] expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(tool.ParametersJsonSchema);
            var properties = doc.RootElement.GetProperty("properties");
            return expected.All(name => properties.TryGetProperty(name, out _)) &&
                   properties.EnumerateObject().Count() == expected.Length;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string[] Required(ITool tool)
    {
        try
        {
            using var doc = JsonDocument.Parse(tool.ParametersJsonSchema);
            return doc.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    private static void Check(string name, bool condition)
    {
        if (condition) { _pass++; } else { _fail++; }
        Console.WriteLine($"    [{(condition ? "PASS" : "FAIL")}] {name}");
    }

    private static void Fail(string name)
    {
        _fail++;
        Console.WriteLine($"    [FAIL] {name}");
    }

    private static void Section(string title) => Console.WriteLine($"---- {title} ----");

    /// <summary>工作区回调：固定根目录。</summary>
    private static Func<string?> Workspace(string root) => () => root;

    /// <summary>规范化成"绝对路径 + 无尾分隔符"，和被测代码里的规则保持一致。</summary>
    private static string Norm(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>两个路径是不是同一个（Windows：大小写不敏感）。</summary>
    private static bool Same(string? actual, string expected) =>
        actual is not null && string.Equals(actual, Norm(expected), StringComparison.OrdinalIgnoreCase);

    /// <summary>返回文本里有没有提到这个路径。</summary>
    private static bool Mentions(ToolResult result, string path) =>
        result.Content.Contains(path, StringComparison.OrdinalIgnoreCase);

    /// <summary>和被测代码同款的千分位格式，用来断言"1,234 bytes"这类文本。</summary>
    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>写文件时用的 UTF-8 无 BOM 编码。</summary>
    private static UTF8Encoding NoBom => new(encoderShouldEmitUTF8Identifier: false);

    private static void Cleanup(string sandbox)
    {
        try
        {
            if (!Directory.Exists(sandbox))
            {
                return;
            }

            // 别被只读属性卡住（第三节故意造过一个只读文件）。
            foreach (var file in Directory.EnumerateFiles(sandbox, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* 删不掉就交给下面的报告 */ }
            }

            Directory.Delete(sandbox, recursive: true);
        }
        catch (Exception error)
        {
            Console.WriteLine($"    （沙箱没删干净，请手工删 {sandbox}: {error.GetType().Name}: {error.Message}）");
        }
    }

    private static int Report()
    {
        Console.WriteLine("==================================================================");
        Console.WriteLine($"PASS {_pass} / FAIL {_fail}");
        Console.WriteLine("==================================================================");
        return _fail == 0 ? 0 : 1;
    }
}
