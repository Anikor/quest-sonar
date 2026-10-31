# Обучение детектора дверей для шлема на наборе DoorDetect (1213 фото помещений: door, handle, cabinet door, refrigerator door)
# и экспорт в ONNX 416x416 для Unity Sentis. Нужны: ultralytics, torch с CUDA (RTX 4070), набор в ./data (git clone MiguelARD/DoorDetect-Dataset).
# Запуск: python train_doors.py            (~15-25 минут на RTX 4070)
import os, random, shutil, glob

def main():
    os.chdir(os.path.dirname(os.path.abspath(__file__)))
    import torch
    print('torch', torch.__version__, 'cuda', torch.cuda.is_available())
    device = 0 if torch.cuda.is_available() else 'cpu'

    # 1. разбивка 90/10 на train/val (ссылки на файлы через списки, без копирования)
    imgs = sorted(glob.glob('data/images/*'))
    imgs = [p for p in imgs if os.path.splitext(p)[1].lower() in ('.jpg', '.jpeg', '.png')]
    random.seed(42)
    random.shuffle(imgs)
    n_val = max(50, len(imgs) // 10)
    val, train = imgs[:n_val], imgs[n_val:]
    os.makedirs('split', exist_ok=True)
    with open('split/train.txt', 'w') as f: f.write('\n'.join(os.path.abspath(p) for p in train))
    with open('split/val.txt', 'w') as f: f.write('\n'.join(os.path.abspath(p) for p in val))
    print('train', len(train), 'val', len(val))

    # ultralytics ищет метки, заменяя /images/ на /labels/ в пути картинки: в DoorDetect так и есть
    with open('split/doordetect.yaml', 'w', encoding='utf-8') as f:
        f.write(f"path: {os.path.abspath('.')}\n")
        f.write("train: split/train.txt\nval: split/val.txt\n")
        f.write("names:\n  0: door\n  1: handle\n  2: cabinet door\n  3: refrigerator door\n")

    # 2. обучение: YOLOv8n от весов COCO, 60 эпох, 640 px
    from ultralytics import YOLO
    m = YOLO('yolov8n.pt')
    m.train(data='split/doordetect.yaml', epochs=60, imgsz=640, batch=32, device=device, workers=4,
            project='runs', name='doors', exist_ok=True, patience=20, verbose=False)

    # 3. проверка и экспорт лучших весов в ONNX 416
    best = YOLO('runs/doors/weights/best.pt')
    metrics = best.val(data='split/doordetect.yaml', imgsz=640, device=device, verbose=False)
    print('mAP50 per class:', dict(zip(['door', 'handle', 'cabinet door', 'refrigerator door'], [round(float(v), 3) for v in metrics.box.ap50])))
    p = best.export(format='onnx', opset=15, imgsz=416, simplify=True, dynamic=False)
    print('exported:', p)

    dst_dir = r'C:\Users\Student 06\Desktop\VR ochiki\Assets\Vision\Resources'
    os.makedirs(dst_dir, exist_ok=True)
    shutil.copyfile(p, os.path.join(dst_dir, 'doors-indoor.onnx'))
    with open(os.path.join(dst_dir, 'doors-indoor-door-class.txt'), 'w') as f:
        f.write('0')
    print('copied to', os.path.join(dst_dir, 'doors-indoor.onnx'), ' (класс door = 0)')

if __name__ == '__main__':
    main()
