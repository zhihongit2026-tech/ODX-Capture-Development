#include "odx/arma3_log.hpp"

#include <algorithm>

#include "odx/recorder.hpp"

namespace odx {

const char* const kArma3CameraHeader =
    "t_qpc_ms,view,pos_x,pos_y,pos_z,right_x,right_y,right_z,up_x,up_y,up_z,fwd_x,fwd_y,fwd_z,fov_top,object_fov,game_fps,player_x,player_y,player_z,player_speed_mps,command_menu,action_menu,teammates_visible,teammates_total,player_alive,guard_sample_id,esc_pressed";

bool arma3_frame_ok(std::string_view payload) {
    const auto commas = std::count(payload.begin(), payload.end(), ',');
    return (commas == 14 || commas == 26) && payload.find_first_of("\r\n\"") == std::string_view::npos;
}

Arma3Log::Arma3Log(std::filesystem::path control) : control_(std::move(control)) {}

Arma3Log::~Arma3Log() { close(); }

void Arma3Log::poll() {
    std::string target;
    std::getline(std::ifstream(control_, std::ios::binary), target);
    while (!target.empty() && (target.back() == '\r' || target.back() == ' ')) target.pop_back();
    if (target == current_) {
        if (out_.is_open()) out_.flush();
        return;
    }
    close();
    current_ = target;
    if (target.empty()) return;
    out_.open(std::filesystem::path(std::u8string(target.begin(), target.end())), std::ios::binary);
    if (out_) out_ << kArma3CameraHeader << '\n';
}

void Arma3Log::write(double t_qpc_ms, std::string_view payload) {
    if (!out_.is_open()) return;
    out_ << format_double(t_qpc_ms) << ',' << payload;
    // Older @OdxCapture addons send only camera fields. Pad their rows so the extended CSV remains rectangular.
    if (std::count(payload.begin(), payload.end(), ',') == 14) out_ << ",,,,,,,,,,,,";
    out_ << '\n';
}

void Arma3Log::close() {
    if (!out_.is_open()) return;
    out_ << kArma3EndLine << '\n';
    out_.close();
}

}  // namespace odx
