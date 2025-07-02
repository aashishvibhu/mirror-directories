package me.vbu.mirrordirectories.model;

import lombok.AllArgsConstructor;
import lombok.Getter;

/**
 * Abstract base class representing an item in the file system hierarchy.
 * This can be extended to represent specific types like files or directories.
 */

@Getter @AllArgsConstructor
public abstract class Node {

    private final String name;

    /**
     * Checks if this node represents a directory.
     *
     * @return True if this node is a directory, false if it's a file
     */
    public abstract boolean isDirectory();

    @Override
    public String toString() {
        return name + (isDirectory() ? " [DIR]" : " [FILE]");
    }
}
