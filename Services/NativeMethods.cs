using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;

namespace OsuGrind.Services
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PerformanceResult
    {
        public double PP;
        public double Stars;
        public double MapLength;
        public int MaxCombo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DifficultyAttributes
    {
        public double AR;
        public double CS;
        public double OD;
        public double HP;
    }

    internal static class NativeMethods
    {
        private const string DllName = "rosu_pp_wrapper";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr rosu_create_context([MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void rosu_destroy_context(IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double rosu_calculate_pp(
            IntPtr ctx,
            uint mods,
            int combo,
            int n300,
            int n100,
            int n50,
            int n_misses,
            int passed_objects,
            int slider_end_hits,
            int small_tick_hits,
            int large_tick_hits,
            double clock_rate,
            double ar_override,
            double cs_override,
            double od_override,
            double hp_override,
            int is_lazer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double rosu_calculate_stars(
            IntPtr ctx,
            uint mods,
            int passed_objects,
            double clock_rate,
            double ar_override,
            double cs_override,
            double od_override,
            double hp_override,
            int is_lazer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern PerformanceResult rosu_calculate_pp_if_fc(
            IntPtr ctx, 
            uint mods, 
            double accuracy, 
            double clockRate, 
            double ar_override, 
            double cs_override, 
            double od_override, 
            double hp_override,
            int is_lazer);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double rosu_calculate_bpm(
            IntPtr ctx,
            uint mods);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern DifficultyAttributes rosu_get_difficulty_attributes(
            IntPtr ctx,
            uint mods,
            double clock_rate,
            double ar_override,
            double cs_override,
            double od_override,
            double hp_override);
    }
}
