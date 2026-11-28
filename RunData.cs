using System.Text;

static class RunData
{
    public const string FolderName = "봉황기록";

    public static string Root { get; private set; } = "";
    public static string LogPath => Path.Combine(Root, "실행로그.txt");
    public static string PuzzleRoot => Path.Combine(Root, "허풍선");

    public static void Prepare()
    {
        string? exePath = Environment.ProcessPath;
        string? exeDir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(exeDir))
            exeDir = AppContext.BaseDirectory;

        Root = Path.Combine(exeDir, FolderName);
        Directory.CreateDirectory(PuzzleRoot);

        string guide = Path.Combine(Root, "안내.txt");
        if (!File.Exists(guide))
        {
            File.WriteAllText(guide, """
            이 폴더를 통째로 압축해서 보내면 실행 기록과 허풍선 데이터를 함께 확인할 수 있습니다.

            실행로그.txt
              시작, 이동, 일시정지, 오류가 시간순으로 쌓입니다.

            허풍선/events.jsonl
              허풍선이 끝날 때마다 결과 한 줄이 추가됩니다.

            허풍선/날짜시간/event.json
              그 판의 판독 글자, 자동 입력 횟수, 성공·실패·사용자 마무리

            허풍선/날짜시간/clip.avi
              그 순간 화면 녹화. 영상이 없으면 같은 폴더의 frame-0001.jpg 를 보면 됩니다.

            다람쥐/날짜시간.json 과 .jpg
              도토리를 든 다람쥐를 클릭한 위치와 그 화면입니다.
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
