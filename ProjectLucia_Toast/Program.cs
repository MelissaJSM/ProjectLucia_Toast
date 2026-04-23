using System;
using System.Drawing;            // 캡처용 (NuGet: System.Drawing.Common 설치 필요)
using System.Drawing.Imaging;   // 이미지 포맷용
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices; // DPI 제어용
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Collections.Generic;
using System.Windows.Forms;     // 화면 해상도 및 위치 필터용

namespace ProjectLucia_Toast
{
    class Program
    {
        // OS에게 1:1 진짜 픽셀 해상도를 요청하여 배율(DPI) 무시하고 정확히 캡처
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        static async Task Main(string[] args)
        {
            // 🔥 [추가 1] 전역 예외 처리기: 비동기 작업이나 예상치 못한 곳에서 터진 치명적 에러 잡기
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                try
                {
                    string fatalLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fatal_crash.txt");
                    File.WriteAllText(fatalLogPath, $"[전역 치명적 에러 발생: {DateTime.Now}]\n{e.ExceptionObject}");
                }
                catch { /* 로그를 쓰다 죽는 경우 방지 */ }
            };

            try
            {
                // 프로그램 시작 시 가장 먼저 DPI 인식을 강제합니다.
                SetProcessDPIAware();

                // 🔥 [핵심 최적화] 프로세스 우선순위 강제 낮춤 (게임 키보드 씹힘 완벽 방지)
                System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;

                Console.WriteLine("=== ProjectLucia_Toast (게이밍 최적화 & 동적 캡처) 시작됨 ===");

                int port = 3982;
                UdpClient udpClient = new UdpClient();
                string ipAddress = "127.0.0.1";

                string captureDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Captures");
                if (!Directory.Exists(captureDir)) Directory.CreateDirectory(captureDir);

                // ==========================================
                // [초기 설정] 윈도우 Toast 알림 리스너 준비
                // ==========================================
                UserNotificationListener listener = UserNotificationListener.Current;
                var accessStatus = await listener.RequestAccessAsync();

                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    // 🔥 [추가 2] 권한 거부 시 조용히 꺼지지 않고 강제로 에러를 발생시켜 로그를 남깁니다.
                    throw new UnauthorizedAccessException("윈도우 알림(Toast) 접근 권한이 거부되었습니다. (Windows 설정에서 알림 접근 허용 필요)");
                }

                uint lastNotifId = 0;
                var initialNotifs = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                foreach (var n in initialNotifs) if (n.Id > lastNotifId) lastNotifId = n.Id;

                Console.WriteLine($"[System] 유니티(포트: {port})로 신규 알림 전송 대기 중...");

                // 창의 고유 핸들과 마지막 캡처 시간을 기억하는 장부
                Dictionary<int, DateTime> processedPopups = new Dictionary<int, DateTime>();

                // ==========================================
                // [메인 루프]
                // ==========================================
                while (true)
                {
                    // 1. 윈도우 표준 Toast 알림 (디스코드 등)
                    try
                    {
                        var notifs = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                        foreach (var notif in notifs)
                        {
                            if (notif.Id > lastNotifId)
                            {
                                string appName = notif.AppInfo.DisplayInfo.DisplayName;
                                var textElements = notif.Notification.Visual.Bindings[0].GetTextElements();
                                string title = textElements.Count > 0 ? textElements[0].Text : "";
                                string body = textElements.Count > 1 ? textElements[1].Text : "";

                                if (textElements.Count > 2)
                                    for (int i = 2; i < textElements.Count; i++) body += " " + textElements[i].Text;

                                string message = $"Toast|{appName}|{(string.IsNullOrEmpty(body) ? title : $"{title} - {body}")}";
                                SendToUnity(udpClient, ipAddress, port, message);
                                lastNotifId = notif.Id;
                            }
                        }
                    }
                    catch (Exception) { } // 이 내부의 자잘한 에러는 루프를 멈추지 않게 무시

                    // 2. 카카오톡 팝업 감지 및 영역 캡처
                    try
                    {
                        AutomationElement root = AutomationElement.RootElement;
                        var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);

                        // 메모리 정리 (오래된 창 기록 삭제)
                        var keysToRemove = new List<int>();
                        foreach (var kvp in processedPopups) if ((DateTime.Now - kvp.Value).TotalSeconds > 60) keysToRemove.Add(kvp.Key);
                        foreach (var key in keysToRemove) processedPopups.Remove(key);

                        // 현재 모니터의 실제 해상도를 가져옵니다.
                        int screenWidth = Screen.PrimaryScreen.Bounds.Width;
                        int screenHeight = Screen.PrimaryScreen.Bounds.Height;

                        foreach (AutomationElement win in children)
                        {
                            string className = win.Current.ClassName;

                            if (!string.IsNullOrEmpty(className) && className.StartsWith("EVA_Window") && win.Current.Name != "카카오톡")
                            {
                                var rect = win.Current.BoundingRectangle;

                                // 팝업 기본 크기 필터링
                                if (rect.Width > 0 && rect.Width < 600 && rect.Height > 0 && rect.Height < 400)
                                {
                                    // [위치 필터] 창의 시작 위치가 화면 우측 하단 영역(가로 65%, 세로 65% 이상)일 때만 통과
                                    bool isAtRightBottom = rect.X > (screenWidth * 0.65) && rect.Y > (screenHeight * 0.65);

                                    if (isAtRightBottom)
                                    {
                                        int windowHandle = win.Current.NativeWindowHandle;

                                        if (!processedPopups.ContainsKey(windowHandle) || (DateTime.Now - processedPopups[windowHandle]).TotalSeconds > 10)
                                        {
                                            processedPopups[windowHandle] = DateTime.Now;

                                            // 비동기 캡처 시작 (사진사 스레드)
                                            Task.Run(async () =>
                                            {
                                                try
                                                {
                                                    // 👇 [핵심: 동적 좌표 안정화 추적] 
                                                    double lastY = win.Current.BoundingRectangle.Y;
                                                    int stableCount = 0;

                                                    // 최대 1.5초(30회) 동안만 추적 (무한 루프 방지)
                                                    for (int i = 0; i < 30; i++)
                                                    {
                                                        await Task.Delay(50); // 0.05초 간격으로 짧게 검사

                                                        try
                                                        {
                                                            double currentY = win.Current.BoundingRectangle.Y;

                                                            // Y 좌표가 이전과 똑같다면 (이동을 멈췄다면)
                                                            if (Math.Abs(lastY - currentY) < 1.0)
                                                            {
                                                                stableCount++;
                                                                // 연속 2번 제자리면 완벽히 안착한 것으로 판단!
                                                                if (stableCount >= 2) break;
                                                            }
                                                            else
                                                            {
                                                                stableCount = 0; // 아직 움직이는 중
                                                                lastY = currentY;
                                                            }
                                                        }
                                                        catch { break; } // 창이 스캔 도중 닫히면 루프 탈출
                                                    }

                                                    // 완전히 멈춘 상태의 최종 좌표 확보
                                                    var finalRect = win.Current.BoundingRectangle;
                                                    int x = (int)finalRect.X;
                                                    int y = (int)finalRect.Y;
                                                    int width = (int)finalRect.Width;
                                                    int height = (int)finalRect.Height;

                                                    // 혹시 창이 비정상적으로 작아졌으면 캡처 취소
                                                    if (width <= 0 || height <= 0) return;

                                                    using (Bitmap bmp = new Bitmap(width, height))
                                                    {
                                                        using (Graphics g = Graphics.FromImage(bmp))
                                                        {
                                                            g.CopyFromScreen(x, y, 0, 0, bmp.Size);
                                                        }

                                                        string fileName = $"Kakao_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                                                        string filePath = Path.Combine(captureDir, fileName);
                                                        bmp.Save(filePath, ImageFormat.Png);

                                                        SendToUnity(udpClient, ipAddress, port, $"카카오톡_이미지|{filePath}");
                                                        Console.WriteLine($"[캡처 완료] {fileName} (최종 안착 좌표: {x}, {y})");
                                                    }
                                                }
                                                catch { }
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception) { } // UI Automation 관련 자잘한 충돌 무시

                    // 🔥 [핵심 최적화] 메인 루프 스캔 딜레이 1000ms(1초)로 유지
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                // 🔥 [추가 3] 메인 스레드에서 잡힌 치명적 에러를 텍스트 파일로 저장합니다.
                try
                {
                    string crashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
                    File.WriteAllText(crashLogPath, $"[메인 스레드 에러 발생: {DateTime.Now}]\n{ex.ToString()}");
                }
                catch { /* 로그 쓰다 죽는 것 방지 */ }
            }
        }

        static void SendToUnity(UdpClient client, string ip, int port, string message)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                client.Send(data, data.Length, ip, port);
                Console.WriteLine($"[UDP 전송] {message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UDP 전송 오류] {ex.Message}");
            }
        }
    }
}