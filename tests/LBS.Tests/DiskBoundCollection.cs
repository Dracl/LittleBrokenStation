using Xunit;

namespace LBS.Tests;

/// <summary>
/// 真落盘又真删文件的组排成一队：台账备份与数据交换都在盘上写快照、删旧件，
/// 而 SqliteConnection.ClearAllPools 是进程级的动作，并行跑时谁替谁扣着句柄算不清账。
/// DisableParallelization 让挂了这个 collection 的类彼此串行（与其他类仍可并行）。
/// </summary>
[CollectionDefinition("DiskBound", DisableParallelization = true)]
public sealed class DiskBoundCollection
{
}
