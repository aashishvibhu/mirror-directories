package me.vbu.mirrordirectories.model;

/**
 * Represents a file in the file system.
 * This is a leaf node in the file system hierarchy.
 */
public class FileNode extends Node {

    /**
     * Creates a new FileNode with the given name.
     *
     * @param name The name of the file
     */
    public FileNode(String name) {
        super(name);
    }

    /**
     * Checks if this node represents a directory.
     *
     * @return Always returns false as this is a file
     */
    @Override
    public boolean isDirectory() {
        return false;
    }
}
