using RadioWebControl.Core.Services.Cw;

namespace RadioWebControl.Core.Tests.Cw
{
    public class MorseTableTests
    {
        [Theory]
        [InlineData(".-", "A")]
        [InlineData("-...", "B")]
        [InlineData("...", "S")]
        [InlineData("---", "O")]
        [InlineData("-----", "0")]
        [InlineData("....-", "4")]
        [InlineData("..--..", "?")]
        [InlineData("-..-.", "/")]
        [InlineData("-...-", "=")]
        [InlineData(".-.-.", "+")]
        [InlineData("...-.-", "<SK>")]
        public void Decodes_the_symbols_that_turn_up_on_the_air(string symbol, string expected)
            => Assert.Equal(expected, MorseTable.Decode(symbol));

        [Fact]
        public void Returns_null_for_a_symbol_that_is_not_Morse()
        {
            Assert.Null(MorseTable.Decode("..-.-.-.-.-"));
            Assert.Null(MorseTable.Decode(""));
        }

        [Fact]
        public void Round_trips_a_callsign()
        {
            const string call = "MM5AGM";
            string encoded = MorseTable.EncodeText(call);

            var decoded = string.Concat(encoded.Split(' ').Select(MorseTable.Decode));
            Assert.Equal(call, decoded);
        }

        [Fact]
        public void Separates_characters_with_a_space_and_words_with_a_slash()
        {
            Assert.Equal("-.-. --.-", MorseTable.EncodeText("CQ"));
            Assert.Equal("-.-. --.-/-.. .", MorseTable.EncodeText("CQ DE"));
        }

        [Fact]
        public void Drops_characters_it_cannot_send()
        {
            Assert.Equal(".- .-.. .--.", MorseTable.EncodeText("AL~P"));
        }
    }
}
