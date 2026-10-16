# -*- coding: utf-8 -*-
# 生成 ysk_model_monitor 软件图标：缘狐小狐脸（橙黄毛+琥珀大眼+红小花+双尾）
from PIL import Image, ImageDraw
import math

S = 512
im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)

ORANGE = (240, 166, 70, 255)
ORANGE_D = (216, 140, 52, 255)
CREAM = (255, 246, 230, 255)
PINK = (250, 190, 180, 255)
AMBER = (255, 199, 82, 255)
BLACK = (40, 32, 28, 255)
RED = (214, 58, 66, 255)
WHITE = (255, 255, 255, 255)

# ---- 双尾（右下探头，先画小尾再大尾 = 右尾大、左尾小，缘狐标志；垫在脸后）----
d.ellipse((400, 210, 516, 326), fill=ORANGE_D)        # 小尾（稍暗，最垫后）
d.ellipse((466, 236, 520, 296), fill=CREAM)           # 小尾白尖
d.ellipse((336, 296, 516, 486), fill=ORANGE)          # 大尾（压在小尾下方，两者错开）
d.ellipse((430, 330, 518, 424), fill=CREAM)           # 大尾白尖

# ---- 耳朵 ----
d.polygon([(86, 26), (226, 142), (58, 224)], fill=ORANGE, outline=ORANGE_D)
d.polygon([(426, 26), (286, 142), (454, 224)], fill=ORANGE, outline=ORANGE_D)
d.polygon([(118, 62), (206, 146), (104, 190)], fill=PINK)
d.polygon([(394, 62), (306, 146), (408, 190)], fill=PINK)

# ---- 脸（圆润 + 尖下巴）----
d.ellipse((70, 118, 442, 448), fill=ORANGE)
d.polygon([(182, 396), (330, 396), (256, 492)], fill=ORANGE)
# 两颊蓬松白毛
d.polygon([(74, 300), (150, 330), (86, 372)], fill=CREAM)
d.polygon([(438, 300), (362, 330), (426, 372)], fill=CREAM)

# ---- 口鼻区 ----
d.ellipse((168, 296, 344, 440), fill=CREAM)

# ---- 琥珀大眼 ----
for cx in (178, 334):
    d.ellipse((cx - 40, 232, cx + 40, 316), fill=AMBER)          # 虹膜
    d.ellipse((cx - 17, 252, cx + 17, 306), fill=BLACK)          # 瞳孔
    d.ellipse((cx - 12, 252, cx + 2, 266), fill=WHITE)           # 高光
    d.ellipse((cx + 4, 288, cx + 14, 298), fill=WHITE)           # 下眼睑高光（缘狐特征）
    d.arc((cx - 44, 224, cx + 44, 300), 195, 345, fill=ORANGE_D, width=7)  # 上眼线

# ---- 鼻子 + 嘴 ----
d.polygon([(240, 330), (272, 330), (256, 352)], fill=(120, 70, 60, 255))
d.arc((226, 340, 256, 372), 0, 180, fill=(120, 70, 60, 255), width=5)
d.arc((256, 340, 286, 372), 0, 180, fill=(120, 70, 60, 255), width=5)

# ---- 胡须 ----
for (x0, y0, x1, y1) in [(96, 340, 168, 350), (100, 372, 170, 374), (344, 350, 416, 340), (342, 374, 412, 372)]:
    d.line((x0, y0, x1, y1), fill=(150, 110, 70, 160), width=4)

# ---- 红色小花发饰（左鬓，缘狐标志）----
fx, fy = 104, 168
for k in range(5):
    a = math.radians(90 + k * 72)
    d.ellipse((fx + 26 * math.cos(a) - 17, fy - 26 * math.sin(a) - 17,
               fx + 26 * math.cos(a) + 17, fy - 26 * math.sin(a) + 17), fill=RED)
d.ellipse((fx - 11, fy - 11, fx + 11, fy + 11), fill=WHITE)      # 白花心
d.ellipse((fx - 4, fy + 30, fx + 4, fy + 46), fill=RED)          # 红色小坠

# ---- 输出多尺寸 ico ----
sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (96, 96), (128, 128), (256, 256)]
out = r"D:\WorkStation\@ysk_work\@ysk_模型监控悬浮窗\src\app.ico"
im.save(out, sizes=sizes)
im.resize((128, 128), Image.LANCZOS).save(r"D:\ProgramFiles\Hermes_Desktop\cache\scratch\fox_icon_128.png")
im.resize((32, 32), Image.LANCZOS).save(r"D:\ProgramFiles\Hermes_Desktop\cache\scratch\fox_icon_32.png")
print("ico saved:", out)
