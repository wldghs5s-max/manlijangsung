import AppKit
import Darwin
import Foundation
import Vision

setvbuf(stdout, nil, _IOLBF, 0)

let request = VNRecognizeTextRequest()
request.revision = VNRecognizeTextRequestRevision3
request.recognitionLevel = .accurate
request.recognitionLanguages = ["ko-KR"]
request.usesLanguageCorrection = false

while let line = readLine(strippingNewline: true) {
    if line == "EXIT" {
        break
    }
    defer {
        print("END")
        fflush(stdout)
    }
    let url = URL(fileURLWithPath: line)
    var image = NSImage(contentsOf: url)
    if image == nil {
        for _ in 0..<8 {
            usleep(40_000)
            image = NSImage(contentsOf: url)
            if image != nil { break }
        }
    }
    guard let image else {
        print("ERR 이미지를 열 수 없습니다 \(line)")
        continue
    }
    var rect = CGRect(origin: .zero, size: image.size)
    guard let cgImage = image.cgImage(forProposedRect: &rect, context: nil, hints: nil) else {
        print("ERR 이미지 변환에 실패했습니다")
        continue
    }
    do {
        try VNImageRequestHandler(cgImage: cgImage, options: [:]).perform([request])
    } catch {
        print("ERR \(error.localizedDescription)")
        continue
    }
    for observation in request.results ?? [] {
        guard let candidate = observation.topCandidates(1).first else { continue }
        let text = candidate.string
        var index = text.startIndex
        while index < text.endIndex {
            let next = text.index(after: index)
            let glyph = String(text[index])
            if glyph.unicodeScalars.allSatisfy({ !CharacterSet.whitespacesAndNewlines.contains($0) }),
               let box = try? candidate.boundingBox(for: index..<next) {
                let bounds = box.boundingBox
                print(String(format: "CH %@ %.4f %.4f", glyph, bounds.midX, bounds.midY))
            }
            index = next
        }
    }
}
