# Экспорт обученных весов DoorDetect (runs/detect/runs/doors/weights/best.pt) в ONNX 416 для Unity Sentis.
import os, shutil, glob
os.chdir(os.path.dirname(os.path.abspath(__file__)))

def main():
    from ultralytics import YOLO
    cands = glob.glob('runs/**/best.pt', recursive=True)
    cands.sort(key=os.path.getmtime)
    best_path = cands[-1]
    print('weights:', best_path)
    best = YOLO(best_path)
    metrics = best.val(data='split/doordetect.yaml', imgsz=640, device=0, verbose=False, plots=False)
    names = ['door', 'handle', 'cabinet door', 'refrigerator door']
    print('mAP50 per class:', dict(zip(names, [round(float(v), 3) for v in metrics.box.ap50])))
    p = best.export(format='onnx', opset=15, imgsz=416, simplify=True, dynamic=False)
    print('exported:', p)
    dst_dir = r'C:\Users\Student 06\Desktop\VR ochiki\Assets\Vision\Resources'
    os.makedirs(dst_dir, exist_ok=True)
    shutil.copyfile(p, os.path.join(dst_dir, 'doors-indoor.onnx'))
    with open(os.path.join(dst_dir, 'doors-indoor-door-class.txt'), 'w') as f:
        f.write('0')
    print('copied to', os.path.join(dst_dir, 'doors-indoor.onnx'), '(класс door = 0)')

if __name__ == '__main__':
    main()
