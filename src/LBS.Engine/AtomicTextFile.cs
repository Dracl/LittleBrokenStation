using System.Text;

namespace LBS.Engine;

/// <summary>
/// 文本留档的原子落笔：先写同目录随机名临时文件，再一把替换到位。
/// 中途断电/被杀只留下临时件，正式档要么上一版要么最新版，永不半截——
/// 评论快照与弹幕清单被 artifacts 行登记后不再重采，半截档等于永久丢档，
/// 所以这三处留档口（评论快照、楼中楼回写、弹幕清单）必须走这张门。
/// 姿势与 <c>DanmakuArchiveService.TryWriteAtomic</c> 同源；临时名带随机后缀，
/// 两个写手并发也不互踩（照 CoverThumbs 的 staging 教训改的）。
/// </summary>
public static class AtomicTextFile
{
    /// <summary>原子写入。目标被播放器/杀软占用（IOException）时返 false，旧档原样留着。</summary>
    public static bool TryWrite(string target, string content)
    {
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var staging = target + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(staging, content, new UTF8Encoding(false));
            // 同卷 Move 带 REPLACE 是原子的：读者只会看到旧档或新档两个整脸。
            File.Move(staging, target, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(staging))
                {
                    File.Delete(staging);
                }
            }
            catch (IOException)
            {
                // 临时件清不动只是落灰，不拦正式档。
            }
        }
    }
}
