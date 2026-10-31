using LBS.Infrastructure;

namespace LBS.Tests;

/// <summary>
/// 数据根解析：站长踩过的坑是「起个站怎么数据没了」——其实是 `dotnet run --project src/LBS.Server`
/// 把工作目录切到了项目目录，照老写法就当场在 src/LBS.Server 下自举出一座空站，看的根本不是那座库。
/// 这里钉的是 ResolveDataRoot 这条纯函数（不碰进程 cwd 与环境变量，免得串了别的用例）：
/// 环境变量最大、就近优先、没库才往上找、同名空壳不算库、到仓库根收手、首次开机照旧新建。
/// </summary>
public sealed class DataPathRootTests : IDisposable
{
    private readonly string _root;

    public DataPathRootTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lbs-datapath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // 每个 fixture 都在自己脚下埋一面 .git 旗：上溯探针到此为止，绝不摸到真机的 Temp/用户目录上去。
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    private string Make(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void SeedLibrary(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "station.db"), "fake-ledger");
    }

    [Fact]
    public void 环境变量说了就算_工作目录有库也不理()
    {
        SeedLibrary(Make("data"));
        var forced = Make("elsewhere");

        var (root, hint) = DataPaths.ResolveDataRoot(Make("repo"), forced);

        Assert.Equal(Path.GetFullPath(forced), Path.GetFullPath(root));
        Assert.Null(hint);
    }

    [Fact]
    public void 工作目录下有库就近用它_哪怕上层还有一座()
    {
        SeedLibrary(Make("data")); // 仓库根那座
        var cwd = Make("src/LBS.Server");
        SeedLibrary(Path.Combine(cwd, "data")); // 脚下这座

        var (root, hint) = DataPaths.ResolveDataRoot(cwd, null);

        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "data")), Path.GetFullPath(root));
        Assert.Null(hint);
    }

    [Fact]
    public void 从项目目录起站_往上取仓库根那座并把来路说破()
    {
        SeedLibrary(Make("data"));
        var cwd = Make("src/LBS.Server"); // dotnet run 的落点：脚下没库

        var (root, hint) = DataPaths.ResolveDataRoot(cwd, null);

        Assert.Equal(Path.GetFullPath(Make("data")), Path.GetFullPath(root));
        Assert.NotNull(hint);
        Assert.Contains("LBS_DATA_DIR", hint);
    }

    [Fact]
    public void 上层只有个同名空壳_不当它是库()
    {
        Directory.CreateDirectory(Make("data")); // 有目录没 station.db：空壳
        var cwd = Make("src/LBS.Server");

        var (root, hint) = DataPaths.ResolveDataRoot(cwd, null);

        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "data")), Path.GetFullPath(root));
        Assert.Null(hint);
    }

    [Fact]
    public void 探针到仓库根就收手_不往盘上乱摸()
    {
        // 库在旗外（_root/data），旗插在 src：上溯到 src 就撞旗收手，宁可新建也不跳出自家仓库去捞。
        SeedLibrary(Make("data")); // 这是旗外那座，不该被看到
        Directory.CreateDirectory(Path.Combine(Make("src"), ".git"));
        var cwd = Make("src/LBS.Server");

        var (root, hint) = DataPaths.ResolveDataRoot(cwd, null);

        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "data")), Path.GetFullPath(root));
        Assert.Null(hint);
    }

    [Fact]
    public void 首次开机哪儿都没库_照旧在工作目录下新建()
    {
        var cwd = Make("nowhere");

        var (root, hint) = DataPaths.ResolveDataRoot(cwd, null);

        Assert.Equal(Path.GetFullPath(Path.Combine(cwd, "data")), Path.GetFullPath(root));
        Assert.Null(hint);
    }

    [Fact]
    public void 环境变量是空串也算没说_照旧走推演()
    {
        SeedLibrary(Make("data"));
        var cwd = Make("src/LBS.Server");

        var (root, hint) = DataPaths.ResolveDataRoot(cwd, "   ");

        Assert.Equal(Path.GetFullPath(Make("data")), Path.GetFullPath(root));
        Assert.NotNull(hint);
    }
}
