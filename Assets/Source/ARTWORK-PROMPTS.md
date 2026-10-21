# Codex Pond 原创素材来源

本项目主题图与鱼类精灵由 OpenAI imagegen 在 2026-10-21 生成。生成图是原创概念素材；没有使用《星露谷物语》《喵了个菜！》或其他游戏的图片、角色、纹理、商标或 UI。原始接触表保存在本目录，应用运行时使用从中裁切的独立透明 PNG。

## 六套池塘场景

源文件：`pond-scenes-contact-sheet.png`。按 3 列 × 2 行裁成 500 × 313 像素，分别对应 A、B、C、D、E、FusionCE；渲染预览：`outputs/CodexPond-Theme-Preview.png`。

生成提示词：

> Create six ORIGINAL small desktop game scene diorama assets in a clean strict 3 columns by 2 rows sheet. Transparent background and transparent gutters; no sky rectangle, no opaque outer background, no letters, no interface, no logos, no people. Each of the six separate compositions is a floating irregular landscape cutout with a clear central pond and a compact shoreline, designed to sit on an otherwise transparent desktop window. Cozy 16-bit pixel-art farming RPG atmosphere, restrained palette, crisp chunky pixel clusters, readable at 500x313 pixels, fully original designs. Panel 1: round pond with wooden sign on upper-left bank. Panel 2: long pond with pier entering from upper-right. Panel 3: compact pond with small timber status-post on upper-left and flower patch. Panel 4: secluded forest pond with mushrooms and stones, no buildings. Panel 5: handheld-game-inspired compact pond with curved reed frame and small stones. Panel 6: fusion pond with tiny cottage on upper-right and wood dock on lower-right. All six have visibly distinct silhouettes, shore arrangements and architecture, not recolors. Each scene must remain within its own equal cell with ample transparent empty margin; do not connect the scenes. Pixel-art drawn by hand-like clusters, no soft 3D rendering, no gradients outside the pond, no recognizable franchise assets.

## 二十种鱼类精灵

源文件：`fish-sprite-contact-sheet.png`，按 5 列 × 4 行裁成各自透明 PNG。图像顺序与 [`PondData.cs`](../../PondData.cs) 中 `FishCatalog` 一致：

1. `sunny` 暖阳鲫
2. `mint` 青团鱼
3. `blue` 溪蓝鳑鲏
4. `rose` 桃花鲤
5. `moon` 月光银鱼
6. `gold` 小金鲤
7. `violet` 紫苔鱼
8. `ember` 晚霞鲈
9. `reed` 芦苇鳅
10. `cloud` 云纹小鲦
11. `lantern` 灯影鳊
12. `crystal` 溪石斑
13. `winter-ice` 冰晶银鱼
14. `autumn-carp` 枫影鲤
15. `lotus` 莲心鲫
16. `stone` 苔石鲶
17. `petal-carp` 花瓣锦鲤
18. `firefly-koi` 萤火金鳞
19. `maple-carp` 枫叶鲤
20. `frost-fish` 霜铃鱼

生成提示词：

> Original game asset sprite sheet, 5 columns by 4 rows, exactly 20 distinct small freshwater fish sprites, each centered in its own equal cell, transparent background, no labels, no text, no frame or grid lines. Each fish is a clean readable 16-bit cozy pixel-art RPG sprite, side view facing right, consistent scale and palette, crisp pixel clusters, 64x40 logical-cell proportions, with 3-5 subtle colors and a tiny clean highlight, no shadow outside sprite. Every cell must contain one different recognizable silhouette and markings, in this exact left-to-right top-to-bottom order: 1 warm golden crucian, 2 mint green round fish, 3 slender blue bitterling, 4 coral pink koi, 5 silver moonfish with crescent mark, 6 small gold carp with raised dorsal fin, 7 violet algae fish with spotted back, 8 orange dusk perch, 9 olive reed loach long body, 10 pale cloud minnow with wispy stripes, 11 amber lantern bream with rounded body, 12 teal rock grouper with spotted fin, 13 icy blue silverfish with crystalline dorsal fin, 14 copper maple carp with leaf-like tail, 15 peach lotus fish with petal-shaped fins, 16 moss-gray catfish with whiskers, 17 rare pink petal koi with flowing tail, 18 rare glowing yellow firefly koi with bioluminescent dots, 19 rare vermilion maple carp with dark scale pattern, 20 rare pale cyan frost fish with icy fins. Make all 20 sprites clearly different, do not reuse one shape recolored. Transparent alpha around every fish and enough space so sprites can be cropped individually.

## 切片和可审阅预览

可重复运行的裁切脚本位于工作目录 `work/prepare-pond-assets.ps1`。输出素材已按文件名分开；烟测检查六个主题画面与二十个鱼精灵都有独立图像数据。主题预览和窗口静态预览位于项目上层 `outputs/` 目录。生成提示词约束了原创方向和像素氛围；这些素材不代表任何既有游戏的官方美术风格复刻。
