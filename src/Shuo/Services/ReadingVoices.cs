namespace Shuo.Services;

internal sealed record ReadingVoice(string Name, string Id);

internal static class ReadingVoices
{
    internal static ReadingVoice[] All { get; } =
    [
        new("Vivi 2.0", "zh_female_vv_uranus_bigtts"),
        new("小何 2.0", "zh_female_xiaohe_uranus_bigtts"),
        new("云舟 2.0", "zh_male_m191_uranus_bigtts"),
        new("小天 2.0", "zh_male_taocheng_uranus_bigtts"),
        new("温柔妈妈 2.0", "zh_female_wenroumama_uranus_bigtts"),
        new("刘飞 2.0", "zh_male_liufei_uranus_bigtts"),
        new("魅力苏菲 2.0", "zh_female_sophie_uranus_bigtts"),
        new("清新女声 2.0", "zh_female_qingxinnvsheng_uranus_bigtts"),
        new("知性灿灿 2.0", "zh_female_cancan_uranus_bigtts"),
        new("撒娇学妹 2.0", "zh_female_sajiaoxuemei_uranus_bigtts"),
        new("甜美小源 2.0", "zh_female_tianmeixiaoyuan_uranus_bigtts"),
        new("甜美桃子 2.0", "zh_female_tianmeitaozi_uranus_bigtts"),
        new("爽快思思 2.0", "zh_female_shuangkuaisisi_uranus_bigtts"),
        new("邻家女孩 2.0", "zh_female_linjianvhai_uranus_bigtts"),
        new("少年梓辛 2.0", "zh_male_shaonianzixin_uranus_bigtts"),
        new("Tina老师 2.0", "zh_female_yingyujiaoxue_uranus_bigtts"),
        new("暖阳女声 2.0", "zh_female_kefunvsheng_uranus_bigtts"),
        new("儿童绘本 2.0", "zh_female_xiaoxue_uranus_bigtts"),
        new("解说小明 2.0", "zh_male_jieshuoxiaoming_uranus_bigtts"),
        new("TVB女声 2.0", "zh_female_tvbnv_uranus_bigtts"),
        new("俏皮女声 2.0", "zh_female_qiaopinv_uranus_bigtts"),
        new("邻家男孩 2.0", "zh_male_linjiananhai_uranus_bigtts"),
        new("儒雅青年 2.0", "zh_male_ruyaqingnian_uranus_bigtts"),
        new("温暖阿虎 2.0", "zh_male_wennuanahu_uranus_bigtts"),
        new("高冷御姐 2.0", "zh_female_gaolengyujie_uranus_bigtts"),
        new("反卷青年 2.0", "zh_male_fanjuanqingnian_uranus_bigtts"),
        new("温柔淑女 2.0", "zh_female_wenroushunv_uranus_bigtts"),
        new("活力小哥 2.0", "zh_male_huolixiaoge_uranus_bigtts"),
        new("霸气青叔 2.0", "zh_male_baqiqingshu_uranus_bigtts"),
        new("悬疑解说 2.0", "zh_male_xuanyijieshuo_uranus_bigtts"),
        new("磁性解说男声 2.0", "zh_male_cixingjieshuonan_uranus_bigtts"),
        new("深夜播客 2.0", "zh_male_shenyeboke_uranus_bigtts"),
        new("亲切女声 2.0", "zh_female_qinqienv_uranus_bigtts"),
        new("清爽男大 2.0", "zh_male_qingshuangnanda_uranus_bigtts"),
        new("温柔小哥 2.0", "zh_male_wenrouxiaoge_uranus_bigtts"),
        new("少儿故事 2.0", "zh_female_shaoergushi_uranus_bigtts"),
    ];
}
