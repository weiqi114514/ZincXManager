#pragma once
#include <string_view>
#include "log_level.h"

namespace logging
{
    void log(LogL level, std::string_view message);
}