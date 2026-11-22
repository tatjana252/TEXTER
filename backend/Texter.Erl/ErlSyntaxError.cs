namespace Texter.Erl
{
    public record ErlSyntaxError(int Line, int Column, string Message);
}
