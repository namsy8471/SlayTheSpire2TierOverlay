namespace Sts2Overlay.Vision.Tests;

/// <summary>The recognisers' own scratch pools (<see cref="VisionScratch"/>).</summary>
public class ScratchPoolTests
{
    private static Canvas RandomCanvas(int w, int h, int seed)
    {
        var c = new Canvas(w, h);
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                c.Set(x, y, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        return c;
    }

    [Fact]
    public void A_reused_dirty_table_gives_the_same_samples_as_a_fresh_one()
    {
        Canvas c = RandomCanvas(300, 200, 5);
        var region = new PixelRect(7, 9, 280, 170);
        var pool = new ScratchPool<int>("test", keep: 1, register: false);
        int[] dirty = pool.Rent(1_000_000);
        Array.Fill(dirty, -123456);
        pool.Return(dirty);
        float[] fresh = new float[20 * 10 * 3], reused = new float[20 * 10 * 3];
        using (IntegralImage a = IntegralImage.Build(c.Frame, region, 2))
            a.SampleGrid(new RectF(20.5f, 15.25f, 250, 150), 20, 10, fresh);
        using (IntegralImage b = IntegralImage.Build(c.Frame, region, 2, pool))
            b.SampleGrid(new RectF(20.5f, 15.25f, 250, 150), 20, 10, reused);
        Assert.Equal(fresh.Select(BitConverter.SingleToInt32Bits), reused.Select(BitConverter.SingleToInt32Bits));
        Assert.Same(dirty, pool.Rent(10)); // the table went back to the pool on Dispose
    }

    [Fact]
    public void Pool_hands_out_the_smallest_fit_and_keeps_at_most_its_count()
    {
        var pool = new ScratchPool<int>("test", keep: 2, register: false);
        int[] big = pool.Rent(1000), small = pool.Rent(100);
        Assert.Equal(1000, big.Length); // exact sizes, no power-of-two rounding
        Assert.Equal(100, small.Length);
        pool.Return(big);
        pool.Return(small);
        Assert.Same(big, pool.Rent(50)); // the first of a burst gets the largest
        Assert.Same(small, pool.Rent(50)); // one rented alongside it the smallest that fits
        int[] third = pool.Rent(10); // nothing free: a new array
        pool.Return(big);
        pool.Return(small);
        pool.Return(third); // full: the smallest is dropped for a larger one only
        Assert.Equal(1100 * sizeof(int), pool.HeldBytes());
    }

    [Fact]
    public void Pool_grows_to_the_size_in_use_instead_of_collecting_sizes()
    {
        var pool = new ScratchPool<int>("test", keep: 2, register: false);
        pool.Return(pool.Rent(100)); // e.g. the reward band
        int[] grid = pool.Rent(300); // the grid band does not fit in it: the small one is dropped
        pool.Return(grid);
        Assert.Equal(300 * sizeof(int), pool.HeldBytes());
        Assert.Same(grid, pool.Rent(100));
    }

    [Fact]
    public void Tables_settle_at_the_largest_single_need_plus_the_second_concurrent_one()
    {
        // The recogniser's bands in a bad order: shop (top + bottom row at once), reward, card grid, shop again.
        var pool = new ScratchPool<int>("test", keep: 2, register: false);
        void Shop()
        {
            int[] top = pool.Rent(100), bottom = pool.Rent(40);
            pool.Return(top);
            pool.Return(bottom);
        }
        Shop();
        pool.Return(pool.Rent(150)); // reward band
        pool.Return(pool.Rent(300)); // grid band
        Shop();
        Shop();
        Assert.Equal((300 + 40) * sizeof(int), pool.HeldBytes());
    }

    [Fact]
    public void Release_drops_the_free_arrays()
    {
        var pool = new ScratchPool<float>("test", keep: 3, register: false);
        pool.Return(pool.Rent(10));
        Assert.Equal(40, pool.Release());
        Assert.Equal(0, pool.HeldBytes());
    }
}
