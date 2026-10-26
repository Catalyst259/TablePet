# 0.2.0 动作播放性能

检查日期：2026年10月26日。最终素材为1296张PNG，1976个时间轴帧槽；移动片段按240Hz制作。当前显示查询为2560×1440、119Hz（约120Hz）。程序通过WPF `CompositionTarget.Rendering` 接收显示刷新，没有原先30ms动画定时器造成的约33fps限制。素材采样率与实际屏幕呈现帧率是不同指标。

## 测量方式与实际结果

`tests/TablePet.PlaybackProbe` 在独立无窗口进程中，用应用自己的PNG解码器、缓存和时钟按120Hz/240Hz真实时间播放七个片段。没有创建窗口、托盘或修改用户配置。测试使用短时主动等待提高采样精度；正式桌宠不忙等。

| 参数 | 采样频率 | 同步缓存缺失 | 最大取帧时间 |
| --- | ---: | ---: | ---: |
| 240DIP / 150% | 120Hz | 0 | 5.81ms |
| 240DIP / 150% | 240Hz | 0 | 3.12ms |
| 480DIP / 200% | 120Hz | 1 | 3.75ms |
| 480DIP / 200% | 240Hz | 0 | 2.81ms |

默认240Hz各片段取帧P95最大为0.0502ms，采样间隔P95约4.17ms。最大尺寸参数的唯一同步读取耗时低于120Hz的8.33ms帧间隔。解码像素缓存峰值127.5MiB，独立测试进程工作集约181–188MiB，默认初始预热约302ms。缓存预算128MiB不等于整个程序内存上限。

结果及清单SHA-256保存在 `artifacts/motion-performance/summary.json`、`playback-120-hz.json`、`playback-240-hz.json`；最大尺寸参数在 `largest-pet/` 子目录。

## 实际修复的问题

初次密集素材测试暴露了两个问题：WIC逐帧缩放消耗20–35ms；LRU把提前解码但尚未显示的未来帧当成旧帧回收。修复后以FileStream读取原始512像素PNG，由WPF在显示时缩放，后台提前解码未来约250ms、最多64帧；当前帧、各片段入口和未来帧有缓存保护。alpha命中只保存单通道，RGBA临时数组复用。静态、拖动、隐藏或系统暂停时停止不必要的高频绘制/预读。

## 复现

```powershell
powershell -File tools/benchmark-motion.ps1
powershell -File tools/benchmark-motion.ps1 -HeightDip 480 -DpiScale 2 -ReportDirectory artifacts/motion-performance/largest-pet
```

这是解码、缓存与时间线测试，不是DWM实际窗口呈现帧率、GPU缩放成本、完整应用CPU占用或长期泄漏测试。不同DIP/DPI参数当前都使用原始512像素解码，因此较大参数通过不代表已实测960像素窗口呈现。当前120Hz屏幕无法实际显示240fps，其他显示器仍待实际环境验收。
