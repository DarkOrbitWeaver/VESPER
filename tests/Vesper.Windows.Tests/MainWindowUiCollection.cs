namespace Vesper.Windows.Tests;

// VESPER uses shared GDI+ menu images; two main forms cannot safely construct
// and dispose those shared resources at once in separate STA test threads.
[CollectionDefinition("Main window UI", DisableParallelization = true)]
public sealed class MainWindowUiCollection
{
}
