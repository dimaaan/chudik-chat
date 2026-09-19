using ChudikChat.Core;
using ChudikChat.Core.Transfer;

namespace ChudikChat.Core.Tests;

public class PathSanitizerTests
{
    private static readonly string Root = OperatingSystem.IsWindows()
        ? @"C:\Downloads\ChudikChat\приём"
        : "/tmp/chudik/приём";

    [Theory]
    // Выход за пределы дерева — то, ради чего всё это написано.
    [InlineData("..")]
    [InlineData("../secret")]
    [InlineData("a/../..")]
    [InlineData("a/../../b")]
    [InlineData("a/./b")]
    [InlineData(@"..\secret")]
    [InlineData(@"a\..\..\b")]
    // Абсолютные пути и UNC.
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\System32")]
    [InlineData(@"\\server\share\file")]
    [InlineData("//server/share/file")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData("C:relative")]
    // Сломанная структура пути.
    [InlineData("")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("/a")]
    [InlineData("a/b/")]
    // Windows срезает хвостовые точки и пробелы, и проверка обходится.
    [InlineData("файл.")]
    [InlineData("файл ")]
    [InlineData("папка./файл")]
    [InlineData("...")]
    // Зарезервированные имена устройств.
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL.log")]
    [InlineData("COM1")]
    [InlineData("lpt9.dat")]
    [InlineData("папка/PRN.txt")]
    [InlineData("COM\u00B9")]
    // Запрещённые символы, включая альтернативные потоки данных NTFS.
    [InlineData("файл:поток")]
    [InlineData("файл|команда")]
    [InlineData("файл?")]
    [InlineData("файл*")]
    [InlineData("файл<имя>")]
    [InlineData("файл\"кавычка\"")]
    // Управляющие символы и bidi-подмена.
    [InlineData("файл\nдругой")]
    [InlineData("файл\u0000")]
    [InlineData("файл\u007F")]
    [InlineData("\u202Egnp.exe")]
    [InlineData("папка/\u2066обман\u2069")]
    public void Hostile_paths_are_refused(string relativePath)
    {
        Assert.Throws<ProtocolException>(() => PathSanitizer.Resolve(Root, relativePath));
    }

    [Fact]
    public void Segment_over_255_chars_is_refused()
    {
        Assert.Throws<ProtocolException>(
            () => PathSanitizer.Resolve(Root, new string('я', 256)));
    }

    [Fact]
    public void Path_over_4096_bytes_is_refused()
    {
        var deep = string.Join('/', Enumerable.Repeat(new string('a', 200), 30));

        Assert.Throws<ProtocolException>(() => PathSanitizer.Resolve(Root, deep));
    }

    [Fact]
    public void Null_is_refused()
    {
        Assert.Throws<ProtocolException>(() => PathSanitizer.Resolve(Root, null!));
    }

    [Theory]
    [InlineData("файл.txt")]
    [InlineData("папка/файл.txt")]
    [InlineData("папка/вложенная/файл с пробелами.txt")]
    [InlineData("a/b/c/d/e/f/g/h/i/j/файл")]
    [InlineData("Отчёт 2026 (черновик).pdf")]
    [InlineData("emoji 👋.png")]
    [InlineData("common.name.with.dots.tar.gz")]
    [InlineData("COMMON.txt")]
    [InlineData("console.log")]
    public void Ordinary_paths_land_inside_the_destination(string relativePath)
    {
        var resolved = PathSanitizer.Resolve(Root, relativePath);

        Assert.StartsWith(Root + Path.DirectorySeparatorChar, resolved, PathSanitizer.PathComparison);
    }

    [Fact]
    public void Backslashes_are_accepted_as_separators()
    {
        var resolved = PathSanitizer.Resolve(Root, @"папка\файл.txt");

        Assert.Equal(
            Path.Combine(Root, "папка", "файл.txt"),
            resolved);
    }

    [Fact]
    public void Segments_are_returned_in_order()
    {
        Assert.Equal(["папка", "вложенная", "файл.txt"], PathSanitizer.Validate("папка/вложенная/файл.txt"));
    }

    [Fact]
    public void Manifest_with_a_repeated_path_is_refused()
    {
        string[] manifest = ["папка/файл.txt", "папка/другой.txt", @"папка\файл.txt"];

        var error = Assert.Throws<ProtocolException>(() => PathSanitizer.ValidateManifest(manifest));

        Assert.Contains("дважды", error.Message);
    }

    [Fact]
    public void Manifest_without_repeats_passes()
    {
        string[] manifest = ["a.txt", "папка/b.txt", "папка/вложенная/c.txt"];

        PathSanitizer.ValidateManifest(manifest);
    }

    [Fact]
    public void One_hostile_entry_condemns_the_whole_manifest()
    {
        string[] manifest = ["хороший.txt", "../../.ssh/authorized_keys"];

        Assert.Throws<ProtocolException>(() => PathSanitizer.ValidateManifest(manifest));
    }

    [Fact]
    public void Long_windows_paths_get_the_extended_prefix()
    {
        var full = Path.Combine(Root, new string('a', 300));
        var forFileSystem = PathSanitizer.ForFileSystem(full);

        if (OperatingSystem.IsWindows())
            Assert.StartsWith(@"\\?\", forFileSystem);
        else
            Assert.Equal(full, forFileSystem);
    }

    [Fact]
    public void Short_paths_are_left_alone()
    {
        var full = Path.Combine(Root, "файл.txt");

        Assert.Equal(full, PathSanitizer.ForFileSystem(full));
    }
}
