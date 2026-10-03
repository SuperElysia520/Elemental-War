# FPS — Unity 第一人称射击游戏

基于 **Unity 2022.3.62f3c1** 开发的第一人称射击 (FPS) 游戏项目。

## 玩法概述

- **FPS 射击战斗**：包含多种武器类型的手感调校（后坐力、开镜速度等）
- **丧尸敌人 AI**：具备 Idle / Move / Attack / Dead 状态机的僵尸敌人
- **角色控制系统**：基于状态机的玩家控制器，支持 Idle / Move / Aiming / Hover 状态切换

## 项目结构

```
Assets/
├── Scripts/                  # 核心游戏代码
│   ├── Base/                 # 基础抽象类（MonoBehaviour 单例、状态机、敌人/玩家状态基类）
│   ├── Player/               # 玩家系统（控制器、武器、状态机、弹道）
│   ├── Enemy/                # 敌人系统（僵尸 AI、状态行为）
│   ├── Manager/              # 游戏管理器（GameManager、UIManager、MonoManager）
│   ├── UI/                   # UI 组件（主菜单、血条、提示菜单、TMP 特效）
│   └── Utils/                # 工具类（状态机、头部瞄准目标）
├── Prefabs/                  # 预制体（角色、敌人、UI 组件）
├── Scenes/                   # 场景（GameStart 主菜单、Game 游戏主场景）
├── Resource/                 # 资源（动画、特效、材质、模型、贴图、字体）
├── Plugins/
│   ├── Low Poly FPS Pack/    # FPS 武器与控制器套件（Low Poly 风格）
│   ├── MagicaCloth2/         # 物理布料模拟系统
│   ├── MMD4Mecanim/          # MMD 模型导入 Mecanim 动画管线
│   ├── EffectCore/           # 粒子特效系统（弹孔、爆炸、溅血等）
│   └── YSA Toon/             # 卡通渲染着色器
└── Settings/                 # 项目设置资源
```

## 依赖插件

| 插件 | 用途 |
|------|------|
| **Low Poly FPS Pack** | FPS 武器系统、第一人称控制器、弹道/弹壳/爆炸系统 |
| **MagicaCloth2** | 角色布料/头发物理模拟 |
| **MMD4Mecanim** | MMD (MikuMikuDance) 模型导入与 Mecanim 动画适配 |
| **EffectCore** | 粒子特效（弹道特效、击中溅射、爆炸效果） |
| **YSA Toon** | 卡通渲染着色器 |
| **Cinemachine** | 高级摄像机控制系统 |
| **TextMesh Pro** | 文字渲染 |
| **Input System** | Unity 新输入系统 |

## 开发环境

- **Unity 版本**：2022.3.62f3c1
- **目标平台**：PC (Windows)
- **脚本后端**：Mono / IL2CPP
