namespace YoutubeTerminal.Tui;

/// <summary>Miejsce, do którego renderer wysyła bajty (konsola albo — w testach — bufor w pamięci).</summary>
internal interface ITerminalOutput
{
    void Write(ReadOnlySpan<byte> data);
}
