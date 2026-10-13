using RadioWebControl.Core.Services.Cw;

namespace RadioWebControl.Core.Tests.Cw
{
    public class CwAutoTuneTests
    {
        [Fact]
        public void One_wild_reading_does_not_move_the_dial()
            => Assert.Equal(42.0, CwAutoTune.SettledOffsetHz(new[] { 40.0, 42, 41, 380, 44 }));

        [Fact]
        public void An_even_count_takes_the_middle_two()
            => Assert.Equal(15.0, CwAutoTune.SettledOffsetHz(new[] { 10.0, 20, 0, 30 }));

        [Fact]
        public void No_readings_is_no_answer()
            => Assert.Null(CwAutoTune.SettledOffsetHz(Array.Empty<double>()));

        [Theory]
        [InlineData(9.4)]
        [InlineData(-9.9)]
        [InlineData(0.0)]
        public void A_station_already_on_pitch_leaves_the_dial_alone(double offset)
            => Assert.Null(CwAutoTune.DialMoveHz(offset));

        [Theory]
        [InlineData(115.4, 115)]
        [InlineData(-62.6, -63)]
        [InlineData(10.0, 10)]
        public void A_station_off_pitch_moves_the_dial_by_the_offset(double offset, long move)
            => Assert.Equal(move, CwAutoTune.DialMoveHz(offset));

        [Fact]
        public void No_offset_means_no_move()
            => Assert.Null(CwAutoTune.DialMoveHz(null));

        [Theory]
        [InlineData(12, 250)]    // slow: the operator's floor
        [InlineData(20, 250)]    // asks 200, floor wins
        [InlineData(30, 300)]
        [InlineData(40, 400)]
        [InlineData(60, 500)]    // capped
        [InlineData(0, 250)]     // unknown speed: the floor
        public void Width_follows_the_speed_between_floor_and_cap(double wpm, int hz)
            => Assert.Equal(hz, CwAutoTune.WidthForSpeedHz(wpm, floorHz: 250));

        [Fact]
        public void A_tracker_that_keeps_coming_back_gives_its_tone()
            => Assert.Equal(931.0, CwAutoTune.SteadyToneHz(new[] { 933.0, 932, 930, 921, 931 }));

        [Fact]
        public void A_tracker_hopping_between_stations_gives_nothing()
            => Assert.Null(CwAutoTune.SteadyToneHz(new[] { 930.0, 760, 925, 870, 931 }));

        [Fact]
        public void A_missing_tone_reading_gives_nothing()
            => Assert.Null(CwAutoTune.SteadyToneHz(new[] { 930.0, 0, 931 }));

        [Fact]
        public void A_floor_above_the_cap_wins()
            => Assert.Equal(600, CwAutoTune.WidthForSpeedHz(40, floorHz: 600, capHz: 500));
    }
}
