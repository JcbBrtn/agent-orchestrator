using AgentOrchestrator.Core.Engine;

namespace AgentOrchestrator.Core.Tests;

public class RepetitionDetectorTests
{
    private static (bool Looped, int Cut) Feed(string text, int chunk = 1)
    {
        var detector = new RepetitionDetector();
        for (var i = 0; i < text.Length; i += chunk)
            if (detector.Feed(text.Substring(i, Math.Min(chunk, text.Length - i))))
                return (true, detector.CutIndex);
        return (false, -1);
    }

    [Fact]
    public void Normal_prose_and_short_legitimate_repeats_do_not_trigger()
    {
        Assert.False(Feed("The quick brown fox jumps over the lazy dog. Then it naps for a while in the sun.").Looped);
        Assert.False(Feed("Results:\n---\n| a | b |\n| --- | --- |\nha ha ha ha").Looped);
        Assert.False(Feed(new string('-', 200)).Looped);
    }

    [Fact]
    public void Repeated_phrase_triggers_and_cut_keeps_one_copy_of_the_unit()
    {
        const string prefix = "Intro. ";
        var text = prefix + string.Concat(Enumerable.Repeat("the cat sat ", 10));

        var (looped, cut) = Feed(text, chunk: 3);

        Assert.True(looped);
        Assert.Equal("Intro. the cat sat", text[..cut]);
    }

    [Fact]
    public void Single_word_loop_triggers_after_the_minimum_repeated_length()
    {
        var (looped, cut) = Feed(string.Concat(Enumerable.Repeat("stuck ", 40)));

        Assert.True(looped);
        Assert.Equal("stuck ".Length, cut);
    }
}
